using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Oracle.EntityFrameworkCore;
using SCemail;
using SCemail.Components.Data;
using SCemail.Components.Shared;

var builder = WebApplication.CreateBuilder(args);
// ---------- LOGGING ----------
builder.Logging.ClearProviders();
builder.Logging.AddDebug();     // 🔹 log in Visual Studio → Output → Debug
builder.Logging.AddConsole();   // 🔹 log in Console
// ---------- DB ----------
var connString = builder.Configuration.GetConnectionString("OracleDb")
    ?? throw new InvalidOperationException("Connection string 'OracleDb' non trovata");

builder.Services.AddDbContextFactory<MailDbContext>(opt =>
{

    opt.UseOracle(connString);
    opt.EnableSensitiveDataLogging().LogTo(Console.WriteLine, LogLevel.Information);
});

builder.Services.AddScoped<MailService_NEW>();
builder.Services.AddScoped<PostItService>();
builder.Services.AddScoped<SCemail.Components.Shared.IEmailTasksClient_NEW, SCemail.Components.Shared.EmailTasksClient_NEW>();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = long.MaxValue; // 🚀 permette allegati enormi
});
builder.Services.AddServerSideBlazor()
    .AddHubOptions(options =>
    {
        options.MaximumReceiveMessageSize = 1024L * 1024L * 200L; // 200 MB
    });


// ---------- Servizi applicativi ----------
builder.Services.AddScoped<MailService>();
builder.Services.AddScoped<IEmailTasksClient, EmailTasksClient>();
builder.Services.AddScoped<AccessiService>();
// stessa istanza sia HostedService che servizio iniettabile
builder.Services.AddSingleton<EmailFetchService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EmailFetchService>());
builder.Services.AddHostedService<ImapHealthCheckService>();
builder.Services.AddSingleton<RubricaImportService>();

builder.Services.AddScoped<IEmailTasksRepository, EmailTasksRepository>();

// ---------- HttpClient sicuro anche nei controller ----------
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient("server", (sp, client) =>
{
    var ctx = sp.GetService<IHttpContextAccessor>()?.HttpContext;

    if (ctx != null)
        client.BaseAddress = new Uri($"{ctx.Request.Scheme}://{ctx.Request.Host}/");
    else
        client.BaseAddress = new Uri(builder.Configuration["AppBaseUrl"] ?? "http://localhost:5298/");
});

// quando qualcuno chiede HttpClient, usa il named client "server"
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("server"));
builder.Services.Configure<AttachmentsOptions>(builder.Configuration.GetSection("Attachments"));
builder.Services.AddScoped<IEmailAttachmentsRepository, EmailAttachmentsRepository>();

// ---------- Blazor / MVC ----------
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddControllers(); // per AttachmentsController & co.

// ---------- MudBlazor ----------
builder.Services.AddMudServices();

var app = builder.Build();

// ---------- Endpoint extra ----------
app.MapPost("/api/mail/fetch-now", async (EmailFetchService svc, CancellationToken ct) =>
{
    var night = svc.IsNightNow();
    await svc.ProcessAllMailboxes(ct, night);
    return Results.Ok(new { night });
});

// ---------- Error handling ----------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    try
    {
        var ex = e.ExceptionObject as Exception;
        Console.Error.WriteLine(ex?.ToString() ?? "Unhandled exception (null)");
    }
    catch { }
};

TaskScheduler.UnobservedTaskException += (s, e) =>
{
    try
    {
        Console.Error.WriteLine(e.Exception.ToString());
        e.SetObserved();
    }
    catch { }
};

// ---------- Pipeline ----------
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapControllers();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
