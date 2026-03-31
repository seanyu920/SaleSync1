using Microsoft.AspNetCore.Mvc;

namespace SaleSync.Controllers
{
    public class CashierController : Controller
    {
        public IActionResult Dashboard()
        {
            return View("CashierDashboard");
        }
    }
}