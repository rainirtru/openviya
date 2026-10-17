namespace OpenViya.Ops.Core;

public static class VpsKeyFiles
{
    public const string PublicKeySelectedMessage = "Выбран публичный ключ .pub. Для входа нужен приватный файл без .pub, например openviya_vps_ed25519. Приватный ключ остаётся на вашем компьютере.";

    public static bool IsPublicKeyPath(string? path) =>
        path?.Trim().Trim('"').EndsWith(".pub", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed class VpsKeyPassphraseRequiredException : Exception
{
    public VpsKeyPassphraseRequiredException(bool administrative = false) : base(administrative
        ? "Административный SSH-ключ зашифрован. Введите его passphrase в поле рядом с выбранным ключом администратора."
        : "Приватный SSH-ключ зашифрован. Введите пароль этого ключа в поле «Пароль / passphrase» начального SSH-доступа. Это пароль ключа, заданный при его создании.") { }
}
