using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using BotAgendamentoAI.Admin.Realtime;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));
var defaultConnection = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrWhiteSpace(defaultConnection))
{
    builder.Services.PostConfigure<AdminOptions>(options =>
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            options.ConnectionString = defaultConnection.Trim();
        }
    });
    builder.Services.AddSingleton<IAdminRepository, SqlServerAdminRepository>();
    builder.Services.AddHostedService<DashboardSqlServerWatcher>();
}
else
{
    builder.Services.AddSingleton<IAdminRepository, SqliteAdminRepository>();
    builder.Services.AddHostedService<DashboardSqliteWatcher>();
}

builder.Services.AddSignalR();
builder.Services.AddSingleton<IDashboardRealtimeNotifier, SignalRDashboardRealtimeNotifier>();
builder.Services.AddControllersWithViews();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var repository = scope.ServiceProvider.GetRequiredService<IAdminRepository>();
    await repository.InitializeAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");
app.MapHub<DashboardHub>("/hubs/dashboard");

app.Run();
