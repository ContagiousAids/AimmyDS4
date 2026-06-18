using Aimmy2.Class;
using System;
using System.Windows;

namespace Aimmy2
{
    public partial class DS4LogWindow : Window
    {
        public DS4LogWindow()
        {
            InitializeComponent();
            // Bind to the observable collection so the ListBox updates live
            LstLog.ItemsSource = Dictionary.DS4Log;
            Dictionary.DS4Log.CollectionChanged += DS4Log_CollectionChanged;
        }

        private void DS4Log_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            Dispatcher?.BeginInvoke(new Action(() => LstLog.Items.Refresh()));
        }

        protected override void OnClosed(EventArgs e)
        {
            Dictionary.DS4Log.CollectionChanged -= DS4Log_CollectionChanged;
            base.OnClosed(e);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            try { Dictionary.DS4Log.Clear(); } catch { }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
