using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BotAgendamentoAI.Domain;

public static class ServiceCategoryRules
{
    public static readonly IReadOnlyList<string> PreferredCategories = new[]
    {
        "Alvenaria",
        "Hidraulica",
        "Marcenaria",
        "Montagem de Moveis",
        "Serralheria",
        "Eletronicos",
        "Eletrodomesticos",
        "Ar-Condicionado"
    };

    private static readonly HashSet<string> DisallowedCategoryKeys = new(StringComparer.Ordinal)
    {
        "outro",
        "outros",
        "outra",
        "outras",
        "geral",
        "generico",
        "diversos",
        "diverso",
        "sem categoria",
        "nao classificado",
        "nao informado",
        "n a"
    };

    public static string ChooseCategory(string? candidate, string serviceTitle, string? notes = null)
    {
        var direct = CanonicalizeCandidate(candidate);
        if (!string.IsNullOrWhiteSpace(direct) && !IsDisallowedCategory(direct))
        {
            return direct;
        }

        var inferred = InferCategoryFromText($"{serviceTitle} {notes}".Trim());
        if (!string.IsNullOrWhiteSpace(inferred))
        {
            return inferred;
        }

        var fallback = BuildSpecificCategoryFromServiceTitle(serviceTitle);
        if (!IsDisallowedCategory(fallback))
        {
            return fallback;
        }

        return "Servico Especializado";
    }

    public static string? InferCategoryFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = NormalizeKey(text);

        if (normalized.Contains("ar condicionado", StringComparison.Ordinal) ||
            normalized.Contains("split", StringComparison.Ordinal) ||
            normalized.Contains("compressor", StringComparison.Ordinal))
        {
            return "Ar-Condicionado";
        }

        if (normalized.Contains("torneira", StringComparison.Ordinal) ||
            normalized.Contains("vazamento", StringComparison.Ordinal) ||
            normalized.Contains("cano", StringComparison.Ordinal) ||
            normalized.Contains("encanamento", StringComparison.Ordinal) ||
            normalized.Contains("registro", StringComparison.Ordinal) ||
            normalized.Contains("pia", StringComparison.Ordinal) ||
            normalized.Contains("descarga", StringComparison.Ordinal) ||
            normalized.Contains("sifao", StringComparison.Ordinal))
        {
            return "Hidraulica";
        }

        if (normalized.Contains("parede", StringComparison.Ordinal) ||
            normalized.Contains("reboco", StringComparison.Ordinal) ||
            normalized.Contains("alvenaria", StringComparison.Ordinal) ||
            normalized.Contains("cimento", StringComparison.Ordinal) ||
            normalized.Contains("piso", StringComparison.Ordinal) ||
            normalized.Contains("azulejo", StringComparison.Ordinal))
        {
            return "Alvenaria";
        }

        if (normalized.Contains("marcenaria", StringComparison.Ordinal) ||
            normalized.Contains("madeira", StringComparison.Ordinal) ||
            normalized.Contains("armario", StringComparison.Ordinal) ||
            normalized.Contains("porta de madeira", StringComparison.Ordinal))
        {
            return "Marcenaria";
        }

        if (normalized.Contains("montagem", StringComparison.Ordinal) ||
            normalized.Contains("montar movel", StringComparison.Ordinal) ||
            normalized.Contains("guarda roupa", StringComparison.Ordinal) ||
            normalized.Contains("rack", StringComparison.Ordinal) ||
            normalized.Contains("cama", StringComparison.Ordinal))
        {
            return "Montagem de Moveis";
        }

        if (normalized.Contains("serralheria", StringComparison.Ordinal) ||
            normalized.Contains("portao", StringComparison.Ordinal) ||
            normalized.Contains("grade", StringComparison.Ordinal) ||
            normalized.Contains("solda", StringComparison.Ordinal) ||
            normalized.Contains("ferro", StringComparison.Ordinal))
        {
            return "Serralheria";
        }

        if (normalized.Contains("geladeira", StringComparison.Ordinal) ||
            normalized.Contains("microondas", StringComparison.Ordinal) ||
            normalized.Contains("fogao", StringComparison.Ordinal) ||
            normalized.Contains("forno", StringComparison.Ordinal) ||
            normalized.Contains("maquina de lavar", StringComparison.Ordinal) ||
            normalized.Contains("lavadora", StringComparison.Ordinal) ||
            normalized.Contains("secadora", StringComparison.Ordinal) ||
            normalized.Contains("lava loucas", StringComparison.Ordinal))
        {
            return "Eletrodomesticos";
        }

        if (normalized.Contains("tv", StringComparison.Ordinal) ||
            normalized.Contains("televis", StringComparison.Ordinal) ||
            normalized.Contains("notebook", StringComparison.Ordinal) ||
            normalized.Contains("computador", StringComparison.Ordinal) ||
            normalized.Contains("celular", StringComparison.Ordinal) ||
            normalized.Contains("video game", StringComparison.Ordinal) ||
            normalized.Contains("som", StringComparison.Ordinal))
        {
            return "Eletronicos";
        }

        return null;
    }

    public static bool IsDisallowedCategory(string? categoryName)
    {
        var key = NormalizeKey(categoryName);
        return string.IsNullOrWhiteSpace(key) || DisallowedCategoryKeys.Contains(key);
    }

    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lowered = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(lowered.Length);

        foreach (var c in lowered)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c) || c == ' ' || c == '-')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append(' ');
            }
        }

        var cleaned = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return cleaned;
    }

    public static string BuildSpecificCategoryFromServiceTitle(string serviceTitle)
    {
        var normalized = NormalizeKey(serviceTitle);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Servico Especializado";
        }

        var ignored = new HashSet<string>(StringComparer.Ordinal)
        {
            "servico",
            "de",
            "do",
            "da",
            "dos",
            "das",
            "em",
            "para",
            "conserto",
            "reparo",
            "manutencao",
            "instalacao"
        };

        var terms = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(term => !ignored.Contains(term))
            .Take(3)
            .ToArray();

        var baseText = terms.Length == 0 ? normalized : string.Join(' ', terms);
        var title = ToTitleCase(baseText);
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Servico Especializado";
        }

        return title.Length <= 50 ? title : title[..50].Trim();
    }

    private static string? CanonicalizeCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var key = NormalizeKey(candidate);
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return key switch
        {
            "alvenaria" => "Alvenaria",
            "hidraulica" => "Hidraulica",
            "marcenaria" => "Marcenaria",
            "montagem de moveis" => "Montagem de Moveis",
            "montagem moveis" => "Montagem de Moveis",
            "serralheria" => "Serralheria",
            "eletronicos" => "Eletronicos",
            "eletronico" => "Eletronicos",
            "eletrodomesticos" => "Eletrodomesticos",
            "eletrodomestico" => "Eletrodomesticos",
            "ar condicionado" => "Ar-Condicionado",
            "ar-condicionado" => "Ar-Condicionado",
            _ => ToTitleCase(key)
        };
    }

    private static string ToTitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var parts = words[i].Split('-', StringSplitOptions.RemoveEmptyEntries);
            for (var j = 0; j < parts.Length; j++)
            {
                var part = parts[j];
                parts[j] = part.Length == 1
                    ? part.ToUpperInvariant()
                    : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant();
            }

            words[i] = string.Join("-", parts);
        }

        return string.Join(' ', words);
    }
}
