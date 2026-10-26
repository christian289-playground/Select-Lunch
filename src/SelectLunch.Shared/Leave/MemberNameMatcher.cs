using System.Globalization;
using System.Text;
using SelectLunch.Shared.Members;

namespace SelectLunch.Shared.Leave;

/// <summary>
/// 메일에서 뽑은 이름을 채널 구성원 명단과 대조한다. 명단 색인은 생성 때 한 번 만든다.
/// <para>
/// 명단은 호출자가 이미 "현재 채널의 활성 사람 구성원"으로 걸러 넘긴다. 이 클래스는 거르지 않는다.
/// 비교 키는 실명과 별칭이며 NFC 정규화 뒤 공백·눈에 안 보이는 서식 문자를 모두 지우고
/// <see cref="StringComparison.OrdinalIgnoreCase"/>로 비교한다.
/// </para>
/// <para>
/// 1단계는 정확 일치다. 하나라도 있으면 거기서 끝낸다(접미사 후보를 보지 않는다).
/// 정확 일치가 하나도 없을 때만 2단계로 어느 한쪽이 다른 쪽의 접미사(<c>EndsWith</c>)인 키를 모은다.
/// 각 단계에서 서로 다른 SlackUserId가 둘 이상이면 아무도 고르지 않고 <see cref="MemberNameMatchStatus.Ambiguous"/>다.
/// 부분 문자열(Contains)이나 접두 일치는 쓰지 않는다.
/// </para>
/// </summary>
public sealed class MemberNameMatcher
{
    private readonly Dictionary<string, KeyOwner> _index = new(StringComparer.OrdinalIgnoreCase);

    public MemberNameMatcher(IReadOnlyList<RosterPerson> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        foreach (var member in members)
        {
            Add(member.RealName, member);
            Add(member.DisplayName, member);
        }
    }

    public MemberNameMatch Match(string extractedName)
    {
        var input = Normalize(extractedName);
        if (input.Length == 0) return new(MemberNameMatchStatus.None, null);

        if (_index.TryGetValue(input, out var exact))
        {
            return exact.Ambiguous
                ? new(MemberNameMatchStatus.Ambiguous, null)
                : new(MemberNameMatchStatus.Unique, exact.Person);
        }

        RosterPerson? found = null;
        foreach (var (key, owner) in _index)
        {
            if (!key.EndsWith(input, StringComparison.OrdinalIgnoreCase)
                && !input.EndsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (owner.Ambiguous) return new(MemberNameMatchStatus.Ambiguous, null);
            if (found is null) found = owner.Person;
            else if (!string.Equals(found.SlackUserId, owner.Person.SlackUserId, StringComparison.Ordinal))
            {
                return new(MemberNameMatchStatus.Ambiguous, null);
            }
        }

        return found is null
            ? new(MemberNameMatchStatus.None, null)
            : new(MemberNameMatchStatus.Unique, found);
    }

    private void Add(string? name, RosterPerson member)
    {
        var key = Normalize(name);
        if (key.Length == 0) return;

        if (!_index.TryGetValue(key, out var owner))
        {
            _index.Add(key, new KeyOwner(member, false));
        }
        else if (!owner.Ambiguous
            && !string.Equals(owner.Person.SlackUserId, member.SlackUserId, StringComparison.Ordinal))
        {
            _index[key] = owner with { Ambiguous = true };
        }
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var text = ToNfc(value);

        var ignorable = 0;
        foreach (var c in text)
        {
            if (IsIgnorable(c)) ignorable++;
        }

        if (ignorable == 0) return text;
        if (ignorable == text.Length) return string.Empty;

        return string.Create(text.Length - ignorable, text, static (dest, source) =>
        {
            var next = 0;
            foreach (var c in source)
            {
                if (!IsIgnorable(c)) dest[next++] = c;
            }
        });
    }

    /// <summary>메일 글에 짝 없는 서로게이트가 섞여도 예외 대신 원문 그대로 비교한다.</summary>
    private static string ToNfc(string value)
    {
        try
        {
            return value.IsNormalized(NormalizationForm.FormC) ? value : value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    /// <summary>공백과 폭 없는/방향 서식 문자(Cf)는 이름 비교에서 의미가 없다.</summary>
    private static bool IsIgnorable(char c) =>
        char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format;

    /// <summary>같은 키를 가진 첫 구성원. 서로 다른 SlackUserId가 같은 키를 가지면 <c>Ambiguous</c>.</summary>
    private readonly record struct KeyOwner(RosterPerson Person, bool Ambiguous);
}
