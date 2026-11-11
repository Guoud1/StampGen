using System.Security.Cryptography;
using System.Text;

namespace StampGen.Services;

// STAMPGEN — ЗНАК ОХРАНЫ ПРАВ ВЛАДЕЛЬЦА
// © Guoud (iljamakeev@yandex.ru). Все права защищены. Условия: LICENSE.md.
//
// Аттестация владельца (схема «только хеш», v2.3):
//   • в коде хранится ТОЛЬКО SHA-256 секретного ключа автора (константа ниже);
//   • сам ключ нигде не хранится — ни в репозитории, ни в релизах, ни в документации;
//   • владелец доказывает владение, называя ключ: SHA-256(ключ) обязан совпасть
//     с константой OwnerKeySha256;
//   • ключ — длинная случайная строка, подбор по хешу вычислительно невозможен.
// Изменение или удаление этой константы — нарушение целостности информации
// об управлении правами (ст. 1300 ГК РФ; DMCA 17 U.S.C. §1202).
// Проверка и ротация ключа: docs/OWNER-ATTESTATION.md, tools/owner-hash.*.
internal static class OwnerGuard
{
    /// <summary>SHA-256 (hex, UTF-8) секретного ключа автора. Сам ключ не хранится.</summary>
    public const string OwnerKeySha256 = "15e3520cb220690d5b41f6d05573dcfaee92e4606178f403ed22362f2de60e77";

    /// <summary>
    /// Проверка ключа владельца: SHA-256(ключ) должен совпасть с эталоном.
    /// Используется для аттестации автора при правках кода и в инструменте
    /// ротации (tools/owner-hash.*). Ключ ни при каких условиях не логируется.
    /// </summary>
    public static bool VerifyKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return hash == OwnerKeySha256;
    }

    /// <summary>Печатает знак охраны прав в консоль при каждом запуске (вызов из Program.cs).</summary>
    public static void Announce()
    {
        Console.WriteLine();
        Console.WriteLine("  StampGen © Guoud — iljamakeev@yandex.ru — все права защищены.");
        Console.WriteLine("  Аттестация владельца: SHA-256 ключа (см. docs/OWNER-ATTESTATION.md).");
        Console.WriteLine();
    }
}
