using System;

namespace SaleSync.Services
{
    /// <summary>
    /// Wraps BCrypt password hashing for the app.
    ///
    /// Existing rows in the database currently store plaintext passwords
    /// (a bug fixed here). To avoid locking every existing user out the
    /// moment this ships, Verify() also accepts a legacy plaintext match
    /// and tells the caller to upgrade that row to a real hash on the
    /// next successful login. New accounts are hashed from the start via
    /// Hash().
    /// </summary>
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

        /// <summary>
        /// Verifies a plaintext password against a stored value that may be
        /// either a bcrypt hash (new/updated accounts) or legacy plaintext
        /// (accounts created before this fix).
        /// </summary>
        /// <param name="needsUpgrade">
        /// True if the match was against legacy plaintext — the caller
        /// should re-hash and save the password back to the database.
        /// </param>
        public static bool Verify(string plainPassword, string storedValue, out bool needsUpgrade)
        {
            needsUpgrade = false;

            if (string.IsNullOrEmpty(storedValue) || string.IsNullOrEmpty(plainPassword))
                return false;

            if (LooksLikeBcryptHash(storedValue))
            {
                return BCrypt.Net.BCrypt.Verify(plainPassword, storedValue);
            }

            // Legacy plaintext row — compare directly, flag it for upgrade.
            bool matches = string.Equals(storedValue, plainPassword, StringComparison.Ordinal);
            if (matches) needsUpgrade = true;
            return matches;
        }
    }
}
