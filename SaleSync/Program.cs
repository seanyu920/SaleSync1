using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Shared store settings (branding, receipts, theme, business hours).
builder.Services.AddScoped<SaleSync.Services.StoreSettingsService>();

// 🚀 ADDED FOR SIGNALR: Register the SignalR service engine
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true; // TEMPORARY — remove before real deployment
});

// ⭐ 1. UPDATED SESSION SETTINGS
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(12); // Matches the login duration
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// ⭐ 2. UPDATED AUTHENTICATION ENGINE
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Home/Login";
        options.AccessDeniedPath = "/Home/Index";
        options.ExpireTimeSpan = TimeSpan.FromHours(12); // Shift-long login

        // ⭐ THE BUG FIX: Resets the 12-hour clock every time the cashier interacts with the POS
        options.SlidingExpiration = true;
    });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// MapStaticAssets() below only serves files that existed in wwwroot at build time
// (it works off a compile-time manifest). Files added at runtime — like uploaded
// product photos — aren't in that manifest, so UseStaticFiles() is added here to
// serve those too.
app.UseStaticFiles();

app.UseRouting();

// ⭐ 3. SECURITY MIDDLEWARE (Order is critical!)
app.UseAuthentication(); // Bouncer checks ID
app.UseAuthorization();  // Bouncer checks Role permissions

// Session must come after Routing and usually after Authorization
app.UseSession();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

// 🚀 ADDED FOR SIGNALR: Map the ChatHub websocket route
app.MapHub<SaleSync.Hubs.ChatHub>("/chatHub");

app.Run();