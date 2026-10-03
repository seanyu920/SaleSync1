using Microsoft.AspNetCore.Mvc;
using SaleSync.Services;

namespace SaleSync.ViewComponents
{
    public class ThemeViewComponent : ViewComponent
    {
        private readonly StoreSettingsService _settings;

        public ThemeViewComponent(StoreSettingsService settings)
        {
            _settings = settings;
        }

        public IViewComponentResult Invoke()
        {
            var store = _settings.GetSettings();
            return View(store);
        }
    }
}