using System.ComponentModel.DataAnnotations;

namespace VideoHome.Data
{
    public class User
    {
        [Required]
        public string Username { get; set; }
        [Required]
        public string Password { get; set; }
    }
}