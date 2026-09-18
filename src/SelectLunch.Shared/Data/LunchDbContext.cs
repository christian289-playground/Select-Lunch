using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;

namespace SelectLunch.Shared.Data;

public sealed class LunchDbContext(DbContextOptions<LunchDbContext> options)
    : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Restaurant> Restaurants => Set<Restaurant>();
    public DbSet<LunchPoll> Polls => Set<LunchPoll>();
    public DbSet<PollCandidate> PollCandidates => Set<PollCandidate>();
    public DbSet<PollVote> PollVotes => Set<PollVote>();
    public DbSet<MealRecord> MealRecords => Set<MealRecord>();
    public DbSet<ChannelDay> ChannelDays => Set<ChannelDay>();

    static readonly DateTimeOffset SeedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>기본 카테고리. Id를 고정해야 마이그레이션이 안정적이다.</summary>
    static readonly Category[] BuiltInCategories =
    [
        new() { Id = 1, Name = "한식",   IsBuiltIn = true, CreatedAt = SeedAt },
        new() { Id = 2, Name = "중식",   IsBuiltIn = true, CreatedAt = SeedAt },
        new() { Id = 3, Name = "일식",   IsBuiltIn = true, CreatedAt = SeedAt },
        new() { Id = 4, Name = "양식",   IsBuiltIn = true, CreatedAt = SeedAt },
        new() { Id = 5, Name = "분식",   IsBuiltIn = true, CreatedAt = SeedAt },
        new() { Id = 6, Name = "아시안", IsBuiltIn = true, CreatedAt = SeedAt },
        new() { Id = 7, Name = "기타",   IsBuiltIn = true, CreatedAt = SeedAt },
    ];

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Category>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(50);
            e.HasData(BuiltInCategories);
        });

        b.Entity<Restaurant>(e =>
        {
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.NormalizedName).HasMaxLength(100);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasOne(x => x.Category)
                .WithMany(c => c.Restaurants)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LunchPoll>(e =>
        {
            e.HasIndex(x => new { x.ChannelId, x.Date }).IsUnique();
        });

        b.Entity<PollCandidate>(e =>
        {
            e.HasKey(x => new { x.PollId, x.RestaurantId });
            e.HasOne(x => x.Poll).WithMany(p => p.Candidates).HasForeignKey(x => x.PollId);
            e.HasOne(x => x.Restaurant).WithMany().HasForeignKey(x => x.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PollVote>(e =>
        {
            // 1인 1표. 다시 누르면 기존 행을 갱신한다.
            e.HasIndex(x => new { x.PollId, x.SlackUserId }).IsUnique();
            e.HasOne(x => x.Poll).WithMany(p => p.Votes).HasForeignKey(x => x.PollId);
            e.HasOne(x => x.Restaurant).WithMany().HasForeignKey(x => x.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MealRecord>(e =>
        {
            e.HasIndex(x => new { x.ChannelId, x.Date }).IsUnique();
            e.HasIndex(x => x.Date);   // 추천 알고리즘의 기간 집계용
            e.HasOne(x => x.Restaurant).WithMany().HasForeignKey(x => x.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ChannelDay>(e =>
        {
            e.HasKey(x => new { x.ChannelId, x.Date });
        });
    }
}
