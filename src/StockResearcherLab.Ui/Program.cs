using StockResearcherLab.Ui.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();

// The only surface this application talks to. No data access of its own and no
// reference to Pipeline, so no stage is reachable from here. Data and Npgsql do
// arrive transitively through the Api project reference and are unused [O.3].
var apiBase = builder.Configuration["Api:BaseAddress"] ?? "http://localhost:5180";
builder.Services.AddHttpClient("api", c => c.BaseAddress = new Uri(apiBase));

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>();
app.Run();
