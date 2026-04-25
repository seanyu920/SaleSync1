using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddSession();

// ⭐ 1. ADD THE SECURITY ENGINE HERE ⭐
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Home/Login"; // Where they go if they get kicked out
        options.AccessDeniedPath = "/Home/Index"; // Where they go if they try to access unauthorized pages
        options.ExpireTimeSpan = TimeSpan.FromHours(12); // Logs them out after a 12-hour shift
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

// ⭐ 2. TURN ON THE SECURITY ENGINE HERE (Order is critical!) ⭐
app.UseAuthentication();
app.UseAuthorization();

// Session usually goes after Authorization
app.UseSession();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();