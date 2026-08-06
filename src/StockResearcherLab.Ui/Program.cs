using StockResearcherLab.Ui.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// The only surface this application talks to. No data access of its own.
var apiBase = builder.Configuration["Api:BaseAddress"] ?? "http://localhost:5180";
builder.Services.AddHttpClient("api", c => c.BaseAddress = new Uri(apiBase));

var app = builder.Build();
app.UseStaticFiles();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
