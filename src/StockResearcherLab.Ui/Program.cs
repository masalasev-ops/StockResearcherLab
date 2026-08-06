using StockResearcherLab.Ui.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();

// The only surface this application talks to. No data access of its own, no
// reference to Data and none to Pipeline, so there is nothing here to call.
var apiBase = builder.Configuration["Api:BaseAddress"] ?? "http://localhost:5180";
builder.Services.AddHttpClient("api", c => c.BaseAddress = new Uri(apiBase));

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>();
app.Run();
