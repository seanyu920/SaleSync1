using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaleSync.Models;
using SaleSync.Services;

namespace SaleSync.Controllers
{
    /// <summary>
    /// Staff side of GCash / Maya receipt checking: view the receipt picture, then confirm or reject.
    /// The review panel itself is shown at the top of the Queue page.
    /// </summary>
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public class PaymentVerificationController : Controller
    {
        private readonly PaymentVerificationService _payments;

        public PaymentVerificationController(PaymentVerificationService payments)
        {
            _payments = payments;
        }

        /// <summary>The receipt picture. Staff only, never cached, so customers' receipts stay private.</summary>
        [HttpGet]
        public IActionResult Receipt(int id)
        {
            var receipt = _payments.GetReceipt(id);
            if (receipt == null) return NotFound();

            Response.Headers["Cache-Control"] = "private, no-store";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(receipt.Value.Data, receipt.Value.Type);
        }

        /// <summary>How many payments are waiting. The Queue page checks this to notice new receipts.</summary>
        [HttpGet]
        public IActionResult Count()
        {
            return Json(new { awaiting = _payments.CountAwaiting() });
        }

        [HttpPost]
        public IActionResult Confirm([FromBody] PaymentReviewRequest request)
        {
            if (request == null || request.ProofId <= 0)
                return BadRequest(new { success = false, message = "Invalid request." });

            var result = _payments.Confirm(request.ProofId, CurrentStaffId());
            return result.Ok
                ? Ok(new { success = true, message = result.Message })
                : BadRequest(new { success = false, message = result.Message });
        }

        [HttpPost]
        public IActionResult Reject([FromBody] PaymentReviewRequest request)
        {
            if (request == null || request.ProofId <= 0)
                return BadRequest(new { success = false, message = "Invalid request." });

            string reason = request.Reason?.Trim() ?? "";
            if (reason.Length < 3)
                return BadRequest(new { success = false, message = "Please type a short reason. The customer will see it." });

            var result = _payments.Reject(request.ProofId, CurrentStaffId(), reason);
            return result.Ok
                ? Ok(new { success = true, message = result.Message })
                : BadRequest(new { success = false, message = result.Message });
        }

        private int CurrentStaffId()
        {
            return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int id) ? id : 0;
        }
    }
}
