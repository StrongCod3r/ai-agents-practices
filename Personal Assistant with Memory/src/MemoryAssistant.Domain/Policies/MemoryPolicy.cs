using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MemoryAssistant.Domain.Exceptions;

namespace MemoryAssistant.Domain.Policies;

public static partial class MemoryPolicy
{
    public static string ValidateContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 2000)
            throw DomainRuleException.Invalid(
                "El recuerdo debe contener entre 1 y 2000 caracteres."
            );
        RejectCredentials(content);
        return content.Trim();
    }

    public static void RejectCredentials(string content)
    {
        if (CredentialPattern().IsMatch(content))
            throw DomainRuleException.Invalid("No se pueden guardar credenciales como recuerdos.");
    }

    public static void ValidateCategory(string category)
    {
        if (category is not ("Semantic" or "Episodic"))
            throw DomainRuleException.Invalid("La categoría debe ser Semantic o Episodic.");
    }

    public static bool IsSensitive(string content) => SensitivePattern().IsMatch(content);

    public static string Fingerprint(string content) =>
        Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    Whitespace().Replace(content.Trim().Normalize().ToUpperInvariant(), " ")
                )
            )
        );

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(
        @"(?i)(sk-[a-z0-9_-]{12,}|-----BEGIN .*PRIVATE KEY|\beyJ[a-z0-9_-]+\.[a-z0-9_-]+\.[a-z0-9_-]+|\b(password|contraseña|api[_ -]?key|clave de api|access[_ -]?token|secreto|secret)\s*[:=]|\b(mi contraseña|mi password|mi clave de api|my password)\s+(es|is)\b)"
    )]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(
        @"(?i)\b(salud|diagn[oó]stico|enfermedad|medicaci[oó]n|diabetes|c[aá]ncer|depresi[oó]n|embaraz|religi[oó]n|cat[oó]lic|musulm[aá]n|orientaci[oó]n sexual|homosexual|gay|lesbiana|cuenta bancaria|iban|tarjeta|dni|pasaporte|domicilio|direcci[oó]n|salario|health|diagnos\w*|medication|religion|sexual|bank account|address)\w*"
    )]
    private static partial Regex SensitivePattern();
}
