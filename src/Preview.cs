using System;
using System.Windows.Forms;
namespace SamplingBasemaps
{
    static class Preview
    {
        [STAThread] static void Main() { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new SourceForm(null)); }
    }
}
