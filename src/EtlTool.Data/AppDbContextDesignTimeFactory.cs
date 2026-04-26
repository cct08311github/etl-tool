using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EtlTool.Data;

/// <summary>
/// 讓 `dotnet ef migrations add` 在 class library 專案中也能建立 DbContext 實例。
/// 設計期間使用本機 SQLite 檔，與執行期路徑無關。
/// </summary>
public class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=etltool-design.db")
            .Options;
        return new AppDbContext(options);
    }
}
