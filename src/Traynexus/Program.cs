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
            // 用 initiallyOwned=false 创建，再 WaitOne(0) 抢锁，
            // 这样能捕获 AbandonedMutexException（上个进程崩溃导致锁被遗弃），
            // 让新实例可以正常接管而不是误报"已在运行"。
            _mutex = new Mutex(false, "Global\\Traynexus_SingleInstance_v1");
            bool owned;
            try
            {
                owned = _mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // 上个进程异常退出，mutex 被遗弃；当前线程已获得所有权，继续启动
                owned = true;
            }

            if (!owned)
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
                try { _mutex.ReleaseMutex(); } catch { }
                try { _mutex.Dispose(); } catch { }
            }
        }
    }
}
