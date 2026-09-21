using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Options;
using SelectLunch.Slack;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Handlers;
using SelectLunch.Slack.Options;
using SelectLunch.Slack.Services;
using SelectLunch.Slack.Workers;
using SlackNet;
using SlackNet.Extensions.DependencyInjection;

// 중복 기동을 막는다. 두 인스턴스가 뜨면 자동 메시지가 두 번 나가고 집계가 갈라진다.
if (!SingleInstanceGuard.TryAcquire("slack", out var guard))
{
    Console.Error.WriteLine("이미 실행 중인 인스턴스가 있습니다. 종료합니다.");
    return 1;
}

using (guard)
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

    builder.Services.Configure<LunchOptions>(
        builder.Configuration.GetSection(LunchOptions.SectionName));
    builder.Services.Configure<SlackOptions>(
        builder.Configuration.GetSection(SlackOptions.SectionName));

    var slackOptions = builder.Configuration.GetSection(SlackOptions.SectionName).Get<SlackOptions>()
        ?? new SlackOptions();

    try
    {
        slackOptions.Validate();   // 토큰이 없으면 여기서 즉시 실패시킨다

        // 타임존이 잘못돼 있으면 LunchClock.NowIn이 TimeZoneNotFoundException을 던진다.
        // 스케줄러의 30초 루프 안에서 처음 맞닥뜨리면 catch-all에 삼켜져 같은 오류를
        // 영원히 로그만 남기고 아무 일도 안 하게 된다 — 토큰과 같은 수준으로 여기서
        // 즉시 실패시킨다.
        var lunchOptionsForValidation = builder.Configuration.GetSection(LunchOptions.SectionName).Get<LunchOptions>()
            ?? new LunchOptions();
        LunchClock.NowIn(lunchOptionsForValidation.TimeZone);
    }
    catch (Exception ex) when (ex is InvalidOperationException or TimeZoneNotFoundException)
    {
        // 스택 트레이스 전체는 "토큰이 비었다"는 한 줄짜리 결론을 읽기 어렵게 만든다.
        // SingleInstanceGuard 실패와 같은 수준으로 — 메시지 한 줄, 종료 코드 1.
        Console.Error.WriteLine($"설정이 올바르지 않습니다: {ex.Message}");
        return 1;
    }

    var dbPath = builder.Configuration["Database:Path"] ?? "data/lunch.db";
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

    builder.Services.AddDbContext<LunchDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

    builder.Services.AddScoped(sp => new LunchService(
        sp.GetRequiredService<LunchDbContext>(), slackOptions.ChannelId));
    builder.Services.AddScoped(sp => new LunchAnnouncer(
        sp.GetRequiredService<ISlackApiClient>(),
        sp.GetRequiredService<LunchDbContext>(),
        sp.GetRequiredService<LunchService>(),
        slackOptions.ChannelId));

    builder.Services.AddScoped<LunchSlashCommandHandler>();
    builder.Services.AddScoped<VoteActionHandler>();
    builder.Services.AddScoped<AbstainActionHandler>();
    builder.Services.AddScoped<MealActionHandler>();
    builder.Services.AddScoped<RestaurantModalHandler>();
    builder.Services.AddScoped<PendingActionHandler>();

    builder.Services.AddSlackNet(c => c
        .UseApiToken(slackOptions.BotToken)
        .UseAppLevelToken(slackOptions.AppToken)
        // SlackNet 자체 로깅(재연결 시도·실패, 인증 오류 등)을 이 앱의 로그 파이프라인으로
        // 잇는다. 연결하지 않으면 SlackNet은 NullLogger를 써서 그 정보가 전부 사라진다.
        .UseLogger(sp => new SlackNetLoggerAdapter(sp.GetRequiredService<ILoggerFactory>()))
        .RegisterSlashCommandHandler<LunchSlashCommandHandler>("/lunch")
        .RegisterBlockActionHandler<VoteActionHandler>()
        .RegisterBlockActionHandler<AbstainActionHandler>()
        .RegisterBlockActionHandler<MealActionHandler>()
        .RegisterBlockActionHandler<PendingActionHandler>()
        .RegisterViewSubmissionHandler<RestaurantModalHandler>(RestaurantModal.CallbackId));

    builder.Services.AddHostedService<SocketModeWorker>();
    builder.Services.AddHostedService<SchedulerWorker>();

    var host = builder.Build();

    // 기동 시 스키마 생성/갱신 — 첫 실행에 별도 작업이 필요 없다
    using (var scope = host.Services.CreateScope())
    {
        await scope.ServiceProvider.GetRequiredService<LunchDbContext>().Database.MigrateAsync();
    }

    await host.RunAsync();
    return 0;
}
