using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddControllersWithViews()
    .AddMvcOptions(options =>
    {
        options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
    });


builder.Services.AddScoped<SaleSync.Services.StoreSettingsService>();


builder.Services.AddScoped<SaleSync.Services.InventoryMovementService>();

builder.Services.AddScoped<SaleSync.Services.InventoryDeductionService>();

builder.Services.AddScoped<SaleSync.Services.CategoryService>();

builder.Services.AddScoped<SaleSync.Services.PaymentVerificationService>();

builder.Services.AddScoped<SaleSync.Services.ProductOptionsService>();

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true; 
});


builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(12); 
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});


builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Home/Login";
        options.AccessDeniedPath = "/Home/Index";
        options.ExpireTimeSpan = TimeSpan.FromHours(12); 
        options.SlidingExpiration = true;
    });

var app = builder.Build();

// Create the stock movement table (inventory_movements) up front if it is missing.
// If the database can't be reached here, it is simply tried again the first time it is needed.
using (var startupScope = app.Services.CreateScope())
{
    startupScope.ServiceProvider
        .GetRequiredService<SaleSync.Services.InventoryMovementService>()
        .EnsureSchema();

    // Same idea for the GCash / Maya receipt table (payment_proofs).
    startupScope.ServiceProvider
        .GetRequiredService<SaleSync.Services.PaymentVerificationService>()
        .EnsureSchema();
}


if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    
}


app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication(); 
app.UseAuthorization();  


app.UseSession();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.MapHub<SaleSync.Hubs.ChatHub>("/chatHub");

app.Run();