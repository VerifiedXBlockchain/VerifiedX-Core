using Microsoft.AspNetCore.SignalR;

namespace VerifiedXCore.Models
{
    public class AdjPool
    {
        public string Address { get; set; }        
        public HubCallerContext Context { get; set; }
    }
}
