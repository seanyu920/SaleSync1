using Microsoft.AspNetCore.Mvc;
using SaleSync.Models;
using System.Diagnostics;

namespace SaleSync.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

        public IActionResult AdminDashboard()
        {
            return View();
        }

        [HttpPost]
        public IActionResult Login(string email, string password)
        {
            // SIMPLE LOGIN (hardcoded)
            if (email == "admin@mail.com" && password == "1234")
            {
                ViewBag.Message = "Login successful!";
                ViewBag.Success = true;
                return View("Index");
            }

            // WRONG LOGIN
            ViewBag.Message = "Invalid email or password.";
            return View("Index");
        }
    }
}
