using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

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

app.Run();