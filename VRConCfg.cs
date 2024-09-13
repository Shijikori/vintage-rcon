using System;
using System.Text;

namespace VintageRCon;

public class VRConCfg {
    public int Port {get; set;} = 42425;
    public string IP {get; set;} = null!;
    public string Password {get; set;} = "";
    public int Timeout {get; set;} = 20;
}
