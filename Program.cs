using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using ErpDecompilerAgenticRAG_Mcp.Models;
using ErpDecompilerAgenticRAG_Mcp.Services;
using ErpDecompilerAgenticRAG_Mcp.Data;



var builder = WebApplication.CreateBuilder(args);

// 显式添加配置文件，确保能正确加载（使用 exe 所在目录）
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

//设置命令行参数，命令行参数格式：--mode http --port 5000 --endpoint /mcp或 --mode stdio 
var cmdMode = builder.Configuration.GetValue<string?>("mode");
var mcpMode = !string.IsNullOrEmpty(cmdMode) ? cmdMode : "http"; //如果没有指定模式，默认使用HTTP模式（可通过双击exe直接启动）
var cmdPort = builder.Configuration.GetValue<int?>("port");
var httpPort = cmdPort.HasValue ? cmdPort.Value : 5000; //如果没有指定端口，默认使用5000端口
var endPoint = !string.IsNullOrEmpty(builder.Configuration.GetValue<string?>("endpoint")) ? builder.Configuration.GetValue<string>("endpoint") : "/erp_decompiler_mcp"; //如果没有指定端点，默认使用/erp_decompiler_mcp

if (mcpMode.ToLower() == "http")
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(httpPort); //指示 Kestrel 监听所有网络接口（0.0.0.0）上的 httpPort 端口，意味着任何 IP 地址（包括 localhost 和外部 IP）都能访问该服务。这通常用于容器化部署或需要对外暴露服务的场景。
    });
}
else
{
    // stdio 模式：禁用 Kestrel HTTP 服务器（使用动态端口，实际不会对外暴露）
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(System.Net.IPAddress.Loopback, 0);
    });
}

builder.Services.AddLogging(loggingBuilder => //添加日志记录器，在 stdio 模式下，绝对、绝对不能往 stdout（标准输出流）打印任何日志，否则会彻底破坏 MCP 协议的 JSON-RPC 通信。
{
    loggingBuilder.AddConsole(consoleLogOptions =>
    {
        consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace; //无论启动模式是 stdio 还是 http，所有控制台日志统统写入 stderr（标准错误流）。这样 stdout 始终是干净的，只用于 MCP 协议数据。
    });
});

//绑定配置（从appsettings.json或环境变量中读取）将 appsettings.json 中的 ErpConfig 节绑定到 ErpConfig 类，如果不做这一步，那么结果会返回 ErpConfig 的默认值（空字符串、空字典等），而不是配置文件中的实际值
builder.Services.Configure<ErpConfig>(
    builder.Configuration.GetSection("ErpConfig")
);

//拿到一个ErpConfig实例并注册为单例服务，这样其他地方通过DI注入ErpConfig类时就可以直接拿到配置好的对象
builder.Services.AddSingleton<ErpConfig>(sp =>
    {
        // 直接从 IConfiguration 读取配置，而不是通过 IOptionsMonitor
        var config = new ErpConfig();
        builder.Configuration.GetSection("ErpConfig").Bind(config);

        //如果需要，可以用环境变量覆盖默认值
        // config.DefaultPath = Environment.GetEnvironmentVariable("ERP_DEFAULTPATH") ?? config.DefaultPath;
        // config.AlternativePaths = Environment.GetEnvironmentVariable("ERP_ALTERNATIVEPATHS") ?? config.AlternativePaths;
        // config.MaxCallChainDepth = Environment.GetEnvironmentVariable("ERP_MAXCALLCHAINDEPTH") ?? config.MaxCallChainDepth;

        //配置默认数据库路径，为当前目录下的erp_index.db
        if (string.IsNullOrWhiteSpace(config.DatabasePath))
        {
            config.DatabasePath = Path.Combine(Directory.GetCurrentDirectory(), "erp_index.db");
        }
        return config;
    }
);

// 注册数据库服务（工厂模式）相当于注册数据库上下文工厂，这样需要用到数据库上下文的地方就直接用工厂创建上下文
// builder.Services.AddSingleton<IndexDatabaseService>();
builder.Services.AddDbContextFactory<ErpDbContext>((sp, options) =>
{
    var config = sp.GetRequiredService<ErpConfig>();
    options.UseSqlite($"Data Source={config.DatabasePath}");
}, ServiceLifetime.Scoped);

builder.Services.AddSingleton<DecompilerService>();

// builder.Services.AddSingleton<IndexDatabaseService>();
builder.Services.AddSingleton<IndexDatabaseService>();

// 注册 CacheManagerService
builder.Services.AddSingleton<CacheManagerService>(sp =>
{
    var cacheDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DecompiledCache");
    var logger = sp.GetRequiredService<ILogger<CacheManagerService>>();
    return new CacheManagerService(cacheDirectory, logger);
});

// 注册 MCP 服务
var mcpBuilder = builder.Services.AddMcpServer()
    .WithToolsFromAssembly();

if (mcpMode.ToLower() == "http")
{
    // HTTP 模式：使用 Streamable HTTP 传输（MCP spec 2025-11-25 推荐方式）
    mcpBuilder.WithHttpTransport();
}
else
{
    // stdio 模式：添加 stdio 传输
    mcpBuilder.WithStdioServerTransport();
}

var app = builder.Build();

// 确保数据库已创建
using (var scope = app.Services.CreateScope())
{
    var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ErpDbContext>>();
    using var context = contextFactory.CreateDbContext();
    context.Database.EnsureCreated();
}

// HTTP 模式：映射 MCP 端点（Streamable HTTP，默认路径 /mcp）
if (mcpMode.ToLower() == "http")
{
    app.MapMcp(endPoint!);
}

app.Run();