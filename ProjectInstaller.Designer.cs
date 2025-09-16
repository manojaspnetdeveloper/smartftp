using System.ServiceProcess;

public partial class Service1 : ServiceBase
{
    public Service1()
    {
       // InitializeComponent();
        this.ServiceName = "SmartAttomTaxroll"; // 👈 This must match what you want to see
    }

    protected override void OnStart(string[] args)
    {
        // TODO: Put your startup logic here
    }

    protected override void OnStop()
    {
        // TODO: Put your cleanup logic here
    }
}
