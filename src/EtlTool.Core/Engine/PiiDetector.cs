namespace EtlTool.Core.Engine;

/// <summary>
/// 依欄位名稱啟發式判斷可能含 PII（個人識別資料）。純查表 + regex，不看資料內容。
///
/// Banks 用途：
///   - Mapping 編輯時提示「此欄位可能含 PII，建議 mask 或加密」
///   - schema fetch 時 tag 欄位給 admin review
/// 不是隱私 governance 的全部 — 但是「忘記 mask」事故的第一道便宜防線。
///
/// 設計：
///   - 比對 lowercase 後的欄位名（Oracle 回大寫；MSSQL 案例敏感）
///   - 用「子字串包含」+「邊界匹配」混合：
///       完全匹配：ssn, password, pin, cvv, ...
///       前綴 / 後綴：*_id_card, *_password, email_*, ...
///   - 回傳 PiiKind 而不是 bool — 訊息更精準（密碼 vs 電郵 vs 卡號 vs 地址）
/// </summary>
public static class PiiDetector
{
    public enum PiiKind
    {
        None = 0,
        Password = 1,    // 帳號密碼 / API key
        Email = 2,
        Phone = 3,
        IdCard = 4,      // 身分證 / 護照
        CreditCard = 5,
        Address = 6,
        DateOfBirth = 7,
        Name = 8,        // 姓名
        Salary = 9,      // 薪資 / 收入
        Generic = 99,    // 命中模糊規則但不確定類別
    }

    public sealed record Detection(PiiKind Kind, string Reason);

    // 完全匹配：lowercase 欄位名 == key
    private static readonly Dictionary<string, PiiKind> ExactMatches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["password"] = PiiKind.Password,
        ["pwd"] = PiiKind.Password,
        ["passwd"] = PiiKind.Password,
        ["secret"] = PiiKind.Password,
        ["api_key"] = PiiKind.Password,
        ["apikey"] = PiiKind.Password,
        ["token"] = PiiKind.Password,
        ["pin"] = PiiKind.Password,
        ["cvv"] = PiiKind.CreditCard,
        ["cvv2"] = PiiKind.CreditCard,
        ["ccv"] = PiiKind.CreditCard,

        ["email"] = PiiKind.Email,
        ["e_mail"] = PiiKind.Email,
        ["mail"] = PiiKind.Email,
        ["email_address"] = PiiKind.Email,

        ["phone"] = PiiKind.Phone,
        ["phone_number"] = PiiKind.Phone,
        ["mobile"] = PiiKind.Phone,
        ["mobile_number"] = PiiKind.Phone,
        ["tel"] = PiiKind.Phone,
        ["telephone"] = PiiKind.Phone,
        ["fax"] = PiiKind.Phone,

        ["ssn"] = PiiKind.IdCard,
        ["id_card"] = PiiKind.IdCard,
        ["id_number"] = PiiKind.IdCard,
        ["national_id"] = PiiKind.IdCard,
        ["passport"] = PiiKind.IdCard,
        ["passport_number"] = PiiKind.IdCard,
        ["身分證"] = PiiKind.IdCard,
        ["身分證號"] = PiiKind.IdCard,

        ["credit_card"] = PiiKind.CreditCard,
        ["creditcard"] = PiiKind.CreditCard,
        ["card_number"] = PiiKind.CreditCard,
        ["cardnumber"] = PiiKind.CreditCard,
        ["card_no"] = PiiKind.CreditCard,
        ["pan"] = PiiKind.CreditCard,
        ["iban"] = PiiKind.CreditCard,
        ["account_number"] = PiiKind.CreditCard,

        ["address"] = PiiKind.Address,
        ["addr"] = PiiKind.Address,
        ["zip"] = PiiKind.Address,
        ["zipcode"] = PiiKind.Address,
        ["postal_code"] = PiiKind.Address,
        ["postcode"] = PiiKind.Address,
        ["地址"] = PiiKind.Address,

        ["dob"] = PiiKind.DateOfBirth,
        ["birthday"] = PiiKind.DateOfBirth,
        ["birth_date"] = PiiKind.DateOfBirth,
        ["date_of_birth"] = PiiKind.DateOfBirth,
        ["生日"] = PiiKind.DateOfBirth,

        ["fullname"] = PiiKind.Name,
        ["full_name"] = PiiKind.Name,
        ["first_name"] = PiiKind.Name,
        ["last_name"] = PiiKind.Name,
        ["surname"] = PiiKind.Name,
        ["legal_name"] = PiiKind.Name,
        ["customer_name"] = PiiKind.Name,
        ["姓名"] = PiiKind.Name,

        ["salary"] = PiiKind.Salary,
        ["wage"] = PiiKind.Salary,
        ["income"] = PiiKind.Salary,
        ["annual_salary"] = PiiKind.Salary,
        ["年收入"] = PiiKind.Salary,
        ["薪資"] = PiiKind.Salary,
    };

    // 子字串：lowercase 欄位名 contains key
    // 注意：太短的子字串（"id"）會誤判，故只放有意義的長字串
    private static readonly (string substring, PiiKind kind)[] SubstringMatches =
    {
        ("password", PiiKind.Password),
        ("passwd", PiiKind.Password),
        ("apikey", PiiKind.Password),
        ("api_key", PiiKind.Password),
        ("secret", PiiKind.Password),

        ("email", PiiKind.Email),

        ("phone", PiiKind.Phone),
        ("mobile", PiiKind.Phone),

        ("creditcard", PiiKind.CreditCard),
        ("credit_card", PiiKind.CreditCard),
        ("card_number", PiiKind.CreditCard),
        ("card_no", PiiKind.CreditCard),

        ("national_id", PiiKind.IdCard),
        ("id_card", PiiKind.IdCard),
        ("passport", PiiKind.IdCard),
        ("ssn", PiiKind.IdCard),

        ("address", PiiKind.Address),
        ("zipcode", PiiKind.Address),
        ("postcode", PiiKind.Address),

        ("date_of_birth", PiiKind.DateOfBirth),
        ("birth_date", PiiKind.DateOfBirth),
        ("birthday", PiiKind.DateOfBirth),

        ("salary", PiiKind.Salary),
    };

    public static Detection Inspect(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
            return new Detection(PiiKind.None, "");

        var lowered = columnName.Trim().ToLowerInvariant();

        // 1) 精確匹配最強，先試
        if (ExactMatches.TryGetValue(lowered, out var exact))
            return new Detection(exact, $"欄位名「{columnName}」是常見 {KindLabel(exact)} 命名");

        // 2) 子字串匹配
        foreach (var (sub, kind) in SubstringMatches)
        {
            if (lowered.Contains(sub, StringComparison.Ordinal))
                return new Detection(kind, $"欄位名包含「{sub}」，疑似 {KindLabel(kind)}");
        }

        return new Detection(PiiKind.None, "");
    }

    public static string KindLabel(PiiKind kind) => kind switch
    {
        PiiKind.Password => "密碼/憑證",
        PiiKind.Email => "電子信箱",
        PiiKind.Phone => "電話",
        PiiKind.IdCard => "身分證/護照",
        PiiKind.CreditCard => "信用卡/帳號",
        PiiKind.Address => "地址/郵遞區號",
        PiiKind.DateOfBirth => "出生日期",
        PiiKind.Name => "姓名",
        PiiKind.Salary => "薪資/收入",
        PiiKind.Generic => "可能含 PII",
        _ => "—",
    };

    /// <summary>批次掃一個 schema 的所有欄位，回傳所有命中項目。</summary>
    public static List<(string Column, Detection Detection)> InspectColumns(IEnumerable<string> columns)
    {
        var result = new List<(string, Detection)>();
        foreach (var col in columns)
        {
            var d = Inspect(col);
            if (d.Kind != PiiKind.None) result.Add((col, d));
        }
        return result;
    }
}
