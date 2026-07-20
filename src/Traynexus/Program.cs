using System;
using System.Threading;
using System.Windows.Forms;

namespace Traynexus
{
    static class Program
    {
        // 单实例锁
        private static Mutex _mutex;

        [STAThread]
        static void Main()
        {
            bool createdNew;
            _mutex = new Mutex(true, "Global\\Traynexus_SingleInstance_v1", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("Traynexus 已经在运行了。", "Traynexus",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Application.Run(new TrayContext());
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Traynexus 崩溃",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                GC.KeepAlive(_mutex);
            }
        }
    }
}
