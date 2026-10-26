using SelectLunch.Shared.Leave;
using SelectLunch.Shared.Members;
using SelectLunch.Slack.Leave;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 메일에서 뽑은 이름·기간을 "메뉴 날짜 + 현재 채널 활성 참여자"에 대조하는 규칙. 이 규칙의 목적은 하나다 —
/// <b>휴가자 없음</b>과 <b>알 수 없음</b>을 섞지 않는다. 이름은 전부 합성 값이다.
/// </summary>
public class LeaveDisplayResolverTests
{
    static readonly DateOnly Day = new(2031, 3, 12);

    static readonly RosterPerson Alpha = new("U1", "Alpha Tester", "alpha");
    static readonly RosterPerson Beta = new("U2", "Beta Tester", "");
    static readonly RosterPerson Gamma = new("U3", "Gamma Tester", "gamma-g");

    /// <summary>"Foo"가 둘 다의 접미사라 모호해지는 쌍.</summary>
    static readonly RosterPerson DeltaFoo = new("U4", "Delta Foo", "");
    static readonly RosterPerson EchoFoo = new("U5", "Echo Foo", "");

    static readonly RosterPerson[] Members = [Alpha, Beta, Gamma, DeltaFoo, EchoFoo];

    static LeaveMailParseResult Leave(string name, DateOnly start, DateOnly end) =>
        new([new LeavePeriod(name, start, end)], []);

    static LeaveMailParseResult Unresolved(string name) => new([], [name]);

    static LeaveRosterSnapshot Resolve(params LeaveMailParseResult[] results) =>
        LeaveDisplayResolver.Resolve(Members, results, Day);

    [Theory]
    [InlineData(1, 3, false)]     // 메뉴일 다음 날 시작
    [InlineData(0, 2, true)]      // 메뉴일에 시작(시작일 포함)
    [InlineData(-1, 1, true)]     // 메뉴일이 한가운데
    [InlineData(-2, 0, true)]     // 메뉴일에 끝남(종료일 포함)
    [InlineData(-3, -1, false)]   // 메뉴일 전날 끝남
    public void Leave_period_covers_the_menu_day_inclusively(int startOffset, int endOffset, bool shown)
    {
        var snapshot = Resolve(Leave("Alpha Tester", Day.AddDays(startOffset), Day.AddDays(endOffset)));

        Assert.Equal(LeaveRosterStatus.Available, snapshot.Status);
        if (shown)
            Assert.Equal(["alpha"], snapshot.DisplayNames);
        else
            Assert.Empty(snapshot.DisplayNames);
    }

    [Fact]
    public void Known_absence_of_leave_is_available_with_nobody_not_unavailable()
    {
        var snapshot = Resolve();

        Assert.Equal(LeaveRosterStatus.Available, snapshot.Status);
        Assert.Empty(snapshot.DisplayNames);
    }

    [Fact]
    public void Shows_display_name_when_present_otherwise_real_name()
    {
        var snapshot = Resolve(
            Leave("Alpha Tester", Day, Day),
            Leave("Beta Tester", Day, Day));

        // 정렬은 서수 비교다: 'B'(0x42) < 'a'(0x61).
        Assert.Equal(["Beta Tester", "alpha"], snapshot.DisplayNames);
    }

    [Fact]
    public void Matching_by_display_name_still_shows_the_display_name()
    {
        var snapshot = Resolve(Leave("alpha", Day, Day));

        Assert.Equal(["alpha"], snapshot.DisplayNames);
    }

    [Fact]
    public void Same_person_in_several_mails_is_listed_once()
    {
        var snapshot = Resolve(
            Leave("Alpha Tester", Day, Day),
            Leave("alpha", Day.AddDays(-1), Day.AddDays(1)),
            Leave("Gamma Tester", Day, Day));

        Assert.Equal(["alpha", "gamma-g"], snapshot.DisplayNames);
    }

    [Fact]
    public void Names_of_people_outside_the_channel_are_ignored()
    {
        var snapshot = Resolve(
            Leave("Someone Elsewhere", Day, Day),
            Unresolved("Another Stranger"));

        Assert.Equal(LeaveRosterStatus.Available, snapshot.Status);
        Assert.Empty(snapshot.DisplayNames);
    }

    [Fact]
    public void Ambiguous_name_on_a_period_covering_the_menu_day_is_unknown_not_nobody()
    {
        var ex = Assert.Throws<LeaveLookupException>(() => Resolve(Leave("Foo", Day, Day)));

        Assert.Equal(LeaveLookupFailure.NameAmbiguous, ex.Reason);
    }

    [Fact]
    public void Ambiguous_name_on_a_period_that_does_not_cover_the_menu_day_is_irrelevant()
    {
        var snapshot = Resolve(Leave("Foo", Day.AddDays(1), Day.AddDays(2)));

        Assert.Equal(LeaveRosterStatus.Available, snapshot.Status);
        Assert.Empty(snapshot.DisplayNames);
    }

    [Theory]
    [InlineData("Alpha Tester")]   // 유일하게 맞는 채널 사람
    [InlineData("Foo")]            // 모호하게 맞는 채널 사람 둘
    public void Name_without_a_confirmed_period_that_matches_the_channel_is_unknown(string name)
    {
        var ex = Assert.Throws<LeaveLookupException>(() => Resolve(Unresolved(name)));

        Assert.Equal(LeaveLookupFailure.DatesUnresolved, ex.Reason);
    }

    [Fact]
    public void Period_ending_before_it_starts_is_unknown_for_a_channel_person_and_ignored_for_others()
    {
        var ex = Assert.Throws<LeaveLookupException>(
            () => Resolve(Leave("Alpha Tester", Day.AddDays(1), Day.AddDays(-1))));
        Assert.Equal(LeaveLookupFailure.DatesUnresolved, ex.Reason);

        var snapshot = Resolve(Leave("Someone Elsewhere", Day.AddDays(1), Day.AddDays(-1)));
        Assert.Equal(LeaveRosterStatus.Available, snapshot.Status);
    }

    [Fact]
    public void One_unknown_poisons_the_whole_result_even_when_others_are_known()
    {
        Assert.Throws<LeaveLookupException>(() => Resolve(
            Leave("Gamma Tester", Day, Day),
            Unresolved("Beta Tester")));
    }

    [Fact]
    public void Leave_form_mail_the_parser_could_not_interpret_is_unknown_even_with_known_leave_elsewhere()
    {
        var unreadable = new LeaveMailParseResult([], [], HasUnresolvedContent: true);

        var ex = Assert.Throws<LeaveLookupException>(
            () => Resolve(Leave("Gamma Tester", Day, Day), unreadable));

        Assert.Equal(LeaveLookupFailure.MailUnparsed, ex.Reason);
    }

    [Fact]
    public void Display_name_is_kept_verbatim_so_plain_text_rendering_shows_it_as_written()
    {
        var odd = new RosterPerson("U9", "Zeta Tester", "<!channel> & co");

        var snapshot = LeaveDisplayResolver.Resolve([odd], [Leave("Zeta Tester", Day, Day)], Day);

        Assert.Equal(["<!channel> & co"], snapshot.DisplayNames);
    }

    [Fact]
    public void Blank_display_name_falls_back_to_the_real_name_and_trims()
    {
        var spaced = new RosterPerson("U8", "  Theta Tester ", "   ");

        var snapshot = LeaveDisplayResolver.Resolve([spaced], [Leave("Theta Tester", Day, Day)], Day);

        Assert.Equal(["Theta Tester"], snapshot.DisplayNames);
    }
}
