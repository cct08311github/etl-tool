using EtlTool.App.Auth;

namespace EtlTool.Tests;

public sealed class PasswordPolicyTests
{
    // -----------------------------------------------------------------------
    // 1. Empty password rejected
    // -----------------------------------------------------------------------

    [Fact]
    public void Empty_password_rejected()
    {
        var result = PasswordPolicy.Evaluate("", "alice");
        Assert.False(result.IsStrong);
        Assert.NotNull(result.Reason);
        Assert.False(string.IsNullOrEmpty(result.Reason));
    }

    // -----------------------------------------------------------------------
    // 2. Too short rejected
    // -----------------------------------------------------------------------

    [Fact]
    public void Too_short_rejected()
    {
        // "Aa1!short" = 9 chars, has 4 classes but length < 12
        var result = PasswordPolicy.Evaluate("Aa1!short", "alice");
        Assert.False(result.IsStrong);
        Assert.NotNull(result.Reason);
        Assert.Contains("12", result.Reason!); // reason mentions length (MinLength = 12)
    }

    // -----------------------------------------------------------------------
    // 3. Exactly 12 chars with 4 classes passes
    // -----------------------------------------------------------------------

    [Fact]
    public void Exactly_12_chars_with_3_classes_passes()
    {
        // "Abcdefghij1!" = 12 chars: lower + upper + digit + symbol = 4 classes
        var result = PasswordPolicy.Evaluate("Abcdefghij1!", "bob");
        Assert.True(result.IsStrong);
        Assert.Null(result.Reason);
    }

    // -----------------------------------------------------------------------
    // 4. Only two classes rejected
    // -----------------------------------------------------------------------

    [Fact]
    public void Only_two_classes_rejected()
    {
        // "abcdefghijkl" = 12 chars, only lowercase = 1 class
        var result = PasswordPolicy.Evaluate("abcdefghijkl", "bob");
        Assert.False(result.IsStrong);
        Assert.NotNull(result.Reason);
    }

    // -----------------------------------------------------------------------
    // 5. Three classes passes
    // -----------------------------------------------------------------------

    [Fact]
    public void Three_classes_passes()
    {
        // "Abcdefghijkl1" = 13 chars, lower + upper + digit = 3 classes
        var result = PasswordPolicy.Evaluate("Abcdefghijkl1", "bob");
        Assert.True(result.IsStrong);
        Assert.Null(result.Reason);
    }

    // -----------------------------------------------------------------------
    // 6. Username in password rejected
    // -----------------------------------------------------------------------

    [Fact]
    public void Username_in_password_rejected()
    {
        var result = PasswordPolicy.Evaluate("AlicePass1!ZZ", "alice");
        Assert.False(result.IsStrong);
        Assert.NotNull(result.Reason);
        Assert.Contains("帳號", result.Reason!); // reason mentions username / account
    }

    // -----------------------------------------------------------------------
    // 7. Username in password case insensitive
    // -----------------------------------------------------------------------

    [Fact]
    public void Username_in_password_case_insensitive()
    {
        var result = PasswordPolicy.Evaluate("ALICEpass1!ZZ", "alice");
        Assert.False(result.IsStrong);
        Assert.NotNull(result.Reason);
    }

    // -----------------------------------------------------------------------
    // 8. Username null skips check
    // -----------------------------------------------------------------------

    [Fact]
    public void Username_null_skips_check()
    {
        // "alice" not present (username is null so skip), still strong
        var result = PasswordPolicy.Evaluate("Aa1!StrongPass", null);
        Assert.True(result.IsStrong);
    }

    // -----------------------------------------------------------------------
    // 9. Username empty skips check
    // -----------------------------------------------------------------------

    [Fact]
    public void Username_empty_skips_check()
    {
        var result = PasswordPolicy.Evaluate("Aa1!StrongPass", "");
        Assert.True(result.IsStrong);
    }

    // -----------------------------------------------------------------------
    // 10. Symbols count as a class but lowercase+symbol only = 2 classes → rejected
    // -----------------------------------------------------------------------

    [Fact]
    public void Symbols_count_as_class_but_two_classes_still_rejected()
    {
        // "aaaaaaaaaaaa!" = 13 chars, lowercase + symbol = 2 classes < MinClasses(3)
        var result = PasswordPolicy.Evaluate("aaaaaaaaaaaa!", null);
        Assert.False(result.IsStrong);
    }

    // -----------------------------------------------------------------------
    // 11. Digits only rejected
    // -----------------------------------------------------------------------

    [Fact]
    public void Digits_only_rejected()
    {
        // "123456789012" = 12 chars, only digit class = 1 class
        var result = PasswordPolicy.Evaluate("123456789012", null);
        Assert.False(result.IsStrong);
    }

    // -----------------------------------------------------------------------
    // 12. Mixed case only rejected (lower+upper = 2 classes)
    // -----------------------------------------------------------------------

    [Fact]
    public void Mixed_case_only_rejected()
    {
        // "AbcdefghijKL" = 12 chars, lower + upper = 2 classes < MinClasses(3)
        var result = PasswordPolicy.Evaluate("AbcdefghijKL", null);
        Assert.False(result.IsStrong);
    }
}
