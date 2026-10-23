using System;

namespace SaleSync.Services
{

    public static class PasswordHasher
    {
        public static string Hash(string plainPassword)
        {
            if (string.IsNullOrEmpty(plainPassword))
                throw new ArgumentException("Password cannot be empty.", nameof(plainPassword));

            return BCrypt.Net.BCrypt.HashPassword(plainPassword);
        }

        private static bool LooksLikeBcryptHash(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                   (value.StartsWith("$2a$") || value.StartsWith("$2b$") || value.StartsWith("$2y$"));
        }

     
        public static bool Verify(string plainPassword, string storedValue, out bool needsUpgrade)
        {
            needsUpgrade = false;

            if (string.IsNullOrEmpty(storedValue) || string.IsNullOrEmpty(plainPassword))
                return false;

            if (LooksLikeBcryptHash(storedValue))
            {
                return BCrypt.Net.BCrypt.Verify(plainPassword, storedValue);
            }

 
            bool matches = string.Equals(storedValue, plainPassword, StringComparison.Ordinal);
            if (matches) needsUpgrade = true;
            return matches;
        }
    }
}
