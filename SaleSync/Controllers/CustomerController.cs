using Microsoft.AspNetCore.Mvc;

namespace SaleSync.Controllers
{
        public class CustomerController : Controller
        {
            [HttpGet]
            public IActionResult CustomerLogIn()
            {
            // Tells it to look for Login.cshtml instead of CustomerLogIn.cshtml
            return View("Login");
            }

             // 2. Loads the Create Account Page
            [HttpGet]
            public IActionResult Register()
            {
                return View();
            }
        public IActionResult CustomerOrdering()
        {
            // ASP.NET will now look in Views/Customer/CustomerOrdering.cshtml
            return View();
        }
    }
    }
