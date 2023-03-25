using System.ComponentModel.DataAnnotations;

namespace VideoHome.Data
{
    public class UserConnectioncount
    {
        public string Username { get; set; } = "";
        public int NumConnctions { get; set; }

        public override string ToString()
        {
            var ret = Username;
            if (NumConnctions > 1)
                ret += "*" + NumConnctions;

            return ret;
        }
    }
}