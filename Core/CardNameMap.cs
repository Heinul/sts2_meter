using System.Text.RegularExpressions;

namespace DamageMeterMod.Core;

/// <summary>
/// CardModel/Power 클래스명에서 읽기 좋은 이름을 추출하는 유틸리티.
/// sts2.dll 정적 분석(577개 카드, 271개 파워) 기반.
///
/// 사용법:
///   CardNameMap.GetReadableName("AdaptiveStrike")  → "Adaptive Strike"
///   CardNameMap.GetReadableName("AfterimagePower")  → "Afterimage"
///   CardNameMap.GetReadableName(someType)           → PascalCase 분리
/// </summary>
public static class CardNameMap
{
    // 알려진 접미사 (제거 대상)
    private static readonly string[] KnownSuffixes =
    {
        "Power", "Relic", "Potion", "Model", "Effect",
        "Buff", "Debuff", "Enchantment", "Affliction"
    };

    /// <summary>
    /// 타입 이름에서 읽기 좋은 이름을 추출.
    /// 예: "AdaptiveStrike" → "Adaptive Strike"
    ///     "AfterimagePower" → "Afterimage"
    ///     "BronzeScalesRelic" → "Bronze Scales"
    /// </summary>
    public static string GetReadableName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return "알 수 없음";

        // 네임스페이스 제거
        var name = typeName.Contains('.') ? typeName.Split('.').Last() : typeName;

        // 알려진 접미사 제거
        foreach (var suffix in KnownSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        // PascalCase → 공백 분리
        return SplitPascalCase(name);
    }

    /// <summary>
    /// Type 객체에서 읽기 좋은 이름을 추출.
    /// </summary>
    public static string GetReadableName(Type type)
    {
        return GetReadableName(type.Name);
    }

    /// <summary>
    /// PascalCase 문자열을 공백으로 분리.
    /// "AdaptiveStrike" → "Adaptive Strike"
    /// "AllForOne" → "All For One"
    /// "CreativeAi" → "Creative Ai"
    /// </summary>
    private static string SplitPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (input.Length <= 2) return input;

        // 대문자 앞에 공백 삽입 (연속 대문자는 약어로 처리)
        var result = Regex.Replace(input, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return result;
    }
}
