using AspNetCoreGeneratedDocument;
using Microsoft.AspNetCore.Mvc;

namespace SaleSync.Controllers
{
    public class CashierController : Controller
    {
        public IActionResult Dashboard()
        {
            var role = HttpContext.Session.GetString("Role");

            if (string.IsNullOrEmpty(role))
                return RedirectToAction("Index", "Home");

            if (role != "Cashier")
                return RedirectToAction("Index", "Home");

            return View("CashierDashboard");
        }
    }
}