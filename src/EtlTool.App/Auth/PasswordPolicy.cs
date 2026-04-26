namespace EtlTool.App.Auth;

/// <summary>
/// 銀行密碼強度策略：純函式 + 純規則，方便單元測試。
///
/// 規則：
///   - 長度 ≥ 12
///   - 字元類別至少 3 種：大寫、小寫、數字、符號
///   - 不可包含 username（case-insensitive substring）
///   - 不可在 WeakPasswords blocklist（reuse 由 UserRepository 處理）
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MinClasses = 3;

    public sealed record Evaluation(bool IsStrong, string? Reason);

    public static Evaluation Evaluate(string newPassword, string? username)
    {
        if (string.IsNullOrEmpty(newPassword))
            return new Evaluation(false, "新密碼不可為空");
        if (newPassword.Length < MinLength)
            return new Evaluation(false, $"新密碼至少 {MinLength} 字元");

        var classes = 0;
        if (newPassword.Any(char.IsLower)) classes++;
        if (newPassword.Any(char.IsUpper)) classes++;
        if (newPassword.Any(char.IsDigit)) classes++;
        if (newPassword.Any(c => !char.IsLetterOrDigit(c))) classes++;
        if (classes < MinClasses)
            return new Evaluation(false, $"新密碼需至少包含 {MinClasses} 種字元類別（大寫 / 小寫 / 數字 / 符號）");

        if (!string.IsNullOrEmpty(username) &&
            newPassword.Contains(username, StringComparison.OrdinalIgnoreCase))
            return new Evaluation(false, "新密碼不可包含帳號名稱");

        return new Evaluation(true, null);
    }
}
