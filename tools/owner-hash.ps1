# StampGen — генератор хеша ключа владельца.
# Читает ключ со скрытым вводом, печатает ТОЛЬКО SHA-256 (hex).
# Сам ключ нигде не сохраняется. См. docs/OWNER-ATTESTATION.md.

$secure = Read-Host "Введите ключ владельца (ввод скрыт)" -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}
$bytes = [Text.Encoding]::UTF8.GetBytes($key)
$hash = ([Security.Cryptography.SHA256]::Create().ComputeHash($bytes) |
    ForEach-Object { $_.ToString('x2') }) -join ''
Write-Output ""
Write-Output "SHA-256 ключа владельца:"
Write-Output $hash
Write-Output ""
Write-Output "Вставьте это значение в StampGen/Services/OwnerGuard.cs (константа OwnerKeySha256)."
