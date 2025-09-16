using System.ComponentModel;
using System.Configuration.Install;
using System.ServiceProcess;

[RunInstaller(true)]
public class ProjectInstaller : Installer
{
    private ServiceProcessInstaller processInstaller;
    private ServiceInstaller serviceInstaller;

    public ProjectInstaller()
    {
        processInstaller = new ServiceProcessInstaller();
        serviceInstaller = new ServiceInstaller();

        // Service will run under LocalSystem
        processInstaller.Account = ServiceAccount.LocalSystem;

        // Service Information
        serviceInstaller.ServiceName = "SmartAttomTaxroll";  // 👈 must match Service1.ServiceName
        serviceInstaller.DisplayName = "Smart Attom Tax Parser";
        serviceInstaller.Description = "Taxroll Services";
        serviceInstaller.StartType = ServiceStartMode.Automatic;

        Installers.Add(processInstaller);
        Installers.Add(serviceInstaller);
    }
}
