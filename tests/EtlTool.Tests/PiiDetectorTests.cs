using EtlTool.Core.Engine;

namespace EtlTool.Tests;

public class PiiDetectorTests
{
    [Theory]
    [InlineData("PASSWORD", PiiDetector.PiiKind.Password)]
    [InlineData("password", PiiDetector.PiiKind.Password)]
    [InlineData("pwd", PiiDetector.PiiKind.Password)]
    [InlineData("api_key", PiiDetector.PiiKind.Password)]
    [InlineData("apikey", PiiDetector.PiiKind.Password)]
    [InlineData("token", PiiDetector.PiiKind.Password)]
    [InlineData("pin", PiiDetector.PiiKind.Password)]
    [InlineData("CVV", PiiDetector.PiiKind.CreditCard)]
    public void Password_and_credential_columns_detected(string column, PiiDetector.PiiKind expected)
    {
        Assert.Equal(expected, PiiDetector.Inspect(column).Kind);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("EMAIL")]
    [InlineData("Email_Address")]
    [InlineData("user_email")]            // 子字串匹配
    [InlineData("contact_email_addr")]    // 子字串匹配
    public void Email_columns_detected(string column)
    {
        Assert.Equal(PiiDetector.PiiKind.Email, PiiDetector.Inspect(column).Kind);
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("MOBILE_NUMBER")]
    [InlineData("tel")]
    [InlineData("user_phone")]            // 子字串
    [InlineData("primary_mobile")]        // 子字串
    public void Phone_columns_detected(string column)
    {
        Assert.Equal(PiiDetector.PiiKind.Phone, PiiDetector.Inspect(column).Kind);
    }

    [Theory]
    [InlineData("ssn")]
    [InlineData("SSN")]
    [InlineData("id_card")]
    [InlineData("passport_number")]
    [InlineData("national_id")]
    [InlineData("身分證")]
    public void IdCard_columns_detected(string column)
    {
        Assert.Equal(PiiDetector.PiiKind.IdCard, PiiDetector.Inspect(column).Kind);
    }

    [Theory]
    [InlineData("credit_card")]
    [InlineData("CARD_NUMBER")]
    [InlineData("iban")]
    [InlineData("account_number")]
    [InlineData("PAN")]
    public void CreditCard_columns_detected(string column)
    {
        Assert.Equal(PiiDetector.PiiKind.CreditCard, PiiDetector.Inspect(column).Kind);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("ZIP")]
    [InlineData("postal_code")]
    [InlineData("地址")]
    [InlineData("home_address")]   // 子字串
    public void Address_columns_detected(string column)
    {
        Assert.Equal(PiiDetector.PiiKind.Address, PiiDetector.Inspect(column).Kind);
    }

    [Theory]
    [InlineData("dob")]
    [InlineData("DATE_OF_BIRTH")]
    [InlineData("birthday")]
    [InlineData("生日")]
    public void DateOfBirth_columns_detected(string column)
    {
        Assert.Equal(PiiDetector.PiiKind.DateOfBirth, PiiDetector.Inspect(column).Kind);
    }

    [Theory]
    [InlineData("first_name")]
    [InlineData("LAST_NAME")]
    [InlineData("fullname")]
    [InlineData("customer_name")]
    [InlineData("姓名")]
    public void Name_columns_detected(string column)
    {
        Assert.Equal(PiiDetector.PiiKind.Name, PiiDetector.Inspect(column).Kind);
    }

    [Theory]
    [InlineData("salary")]
    [InlineData("SALARY")]
    [InlineData("annual_salary")]
    [InlineData("薪資")]
    [InlineData("monthly_salary")]   // 子字串
    public void Salary_columns_detected(string column)
    {
        Assert.Equal(PiiDetector.PiiKind.Salary, PiiDetector.Inspect(column).Kind);
    }

    [Theory]
    [InlineData("created_at")]
    [InlineData("ID")]                // 太短，不應命中（避免 false positive on plain "id")
    [InlineData("status")]
    [InlineData("amount")]
    [InlineData("description")]
    [InlineData("order_id")]
    [InlineData("product_code")]
    [InlineData("quantity")]
    public void Non_pii_columns_return_None(string column)
    {
        Assert.Equal(PiiDetector.PiiKind.None, PiiDetector.Inspect(column).Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Empty_or_null_column_returns_None(string? column)
    {
        Assert.Equal(PiiDetector.PiiKind.None, PiiDetector.Inspect(column!).Kind);
    }

    [Fact]
    public void Reason_text_includes_column_name_for_exact_match()
    {
        var result = PiiDetector.Inspect("password");
        Assert.Contains("password", result.Reason);
    }

    [Fact]
    public void Reason_text_indicates_substring_match()
    {
        var result = PiiDetector.Inspect("user_email");
        Assert.Contains("email", result.Reason);
    }

    [Fact]
    public void InspectColumns_filters_to_hits_only()
    {
        var cols = new[] { "id", "password", "amount", "user_email", "status", "ssn" };
        var hits = PiiDetector.InspectColumns(cols);
        Assert.Equal(3, hits.Count);
        Assert.Contains(hits, h => h.Column == "password");
        Assert.Contains(hits, h => h.Column == "user_email");
        Assert.Contains(hits, h => h.Column == "ssn");
    }

    [Fact]
    public void InspectColumns_empty_input_returns_empty()
    {
        var hits = PiiDetector.InspectColumns(Array.Empty<string>());
        Assert.Empty(hits);
    }

    [Theory]
    [InlineData(PiiDetector.PiiKind.None, "—")]
    [InlineData(PiiDetector.PiiKind.Password, "密碼/憑證")]
    [InlineData(PiiDetector.PiiKind.Email, "電子信箱")]
    [InlineData(PiiDetector.PiiKind.IdCard, "身分證/護照")]
    [InlineData(PiiDetector.PiiKind.CreditCard, "信用卡/帳號")]
    public void KindLabel_localized(PiiDetector.PiiKind kind, string expected)
    {
        Assert.Equal(expected, PiiDetector.KindLabel(kind));
    }
}
