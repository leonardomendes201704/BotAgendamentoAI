using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using BotAgendamentoAI.Admin.Realtime;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));
builder.Services.AddSingleton<IAdminRepository, SqliteAdminRepository>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IDashboardRealtimeNotifier, SignalRDashboardRealtimeNotifier>();
builder.Services.AddHostedService<DashboardSqliteWatcher>();
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
