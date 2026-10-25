using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.UI.ViewModels;

namespace LalabAutoReport.UI.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    private DashboardViewModel? ViewModel => DataContext as DashboardViewModel;

    private void FilterAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.CurrentFilter = "All";
    }

    private void FilterRegular_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.CurrentFilter = "Regular";
    }

    private void FilterGuest_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.CurrentFilter = "Guest";
    }

    private void FilterNeedsReview_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.CurrentFilter = "NeedsReview";
    }

    private void FilterReady_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.CurrentFilter = "Ready";
    }

    private void FilterBilled_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.CurrentFilter = "Billed";
    }

    private void FilterLocked_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.CurrentFilter = "Billed";
    }

    private void FilterUndelivered_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.CurrentFilter = "Undelivered";
    }

    private void FilterDelivered_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.CurrentFilter = "Delivered";
    }

    private void OpenOrderFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is OrderDisplayModel order && ViewModel != null)
        {
            ViewModel.OpenOrderFolderCommand.Execute(order);
        }
    }


    private void DeleteOrder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is OrderDisplayModel order && ViewModel != null)
        {
            ViewModel.DeleteOrderCommand.Execute(order);
        }
    }

    private void ComputeOrViewBill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is OrderDisplayModel order && ViewModel != null)
        {
            ViewModel.ComputeOrViewBillCommand.Execute(order);
        }
    }

    public void OrderCard_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep && FindVisualParent<Button>(dep) != null)
        {
            return;
        }

        if (sender is ListBoxItem item && item.DataContext is OrderDisplayModel order && ViewModel != null)
        {
            ViewModel.ComputeOrViewBillCommand.Execute(order);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void OpenSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is OrderItemDisplayModel item && ViewModel != null)
        {
            ViewModel.OpenSourceFolderCommand.Execute(item);
        }
    }

    private void OpenPrintFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is OrderItemDisplayModel item && ViewModel != null)
        {
            ViewModel.OpenPrintFolderCommand.Execute(item);
        }
    }

    private void RescanSpec_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is OrderItemDisplayModel item && ViewModel != null)
        {
            ViewModel.RescanSpecificationCommand.Execute(item);
        }
    }

    private void SelectCandidateFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is string candidatePath && ViewModel != null)
        {
            // Find parent OrderItemDisplayModel
            var parent = FindParent<Border>(btn);
            if (parent?.DataContext is OrderItemDisplayModel item)
            {
                ViewModel.SelectCandidatePrintFolderCommand.Execute((item, candidatePath));
            }
        }
    }

    private void UsePrintQuantity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is OrderItemDisplayModel item && ViewModel != null)
        {
            if (item.PrintCount.HasValue)
            {
                ViewModel.ResolveQuantityCommand.Execute((item, QuantityResolutionMode.UsePrint, item.PrintCount.Value, (string?)"Dùng số lượng in"));
            }
        }
    }

    private void UseSourceQuantity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is OrderItemDisplayModel item && ViewModel != null)
        {
            ViewModel.ResolveQuantityCommand.Execute((item, QuantityResolutionMode.UseSource, item.SourceCount, (string?)"Dùng số lượng gốc"));
        }
    }

    private void CustomQuantity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is OrderItemDisplayModel item && ViewModel != null)
        {
            var window = Window.GetWindow(this);
            string? input = InputDialog.Show(
                window,
                "Tùy chỉnh số lượng tính tiền",
                $"Nhập số lượng tính tiền tùy chỉnh cho quy cách '{item.SpecName}':",
                (item.PrintCount ?? item.SourceCount).ToString()
            );

            if (!string.IsNullOrWhiteSpace(input) && int.TryParse(input, out int customQty) && customQty >= 0)
            {
                ViewModel.ResolveQuantityCommand.Execute((item, QuantityResolutionMode.Custom, customQty, (string?)"Người dùng nhập thủ công"));
            }
        }
    }

    public void OrderCodeBadge_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is OrderDisplayModel order && !string.IsNullOrWhiteSpace(order.OrderCode))
        {
            try
            {
                Clipboard.SetText(order.OrderCode);
                if (ViewModel != null)
                {
                    ViewModel.StatusMessage = $"Đã sao chép mã đơn: {order.OrderCode}";
                }
            }
            catch
            {
                // Clipboard access might rarely fail
            }
        }
    }

    private void Thumbnail_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is OrderDisplayModel order)
        {
            OpenGalleryForOrder(order.Id);
            this.Focus();
        }
    }

    private void OpenOrderGallery_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is OrderDisplayModel order)
        {
            OpenGalleryForOrder(order.Id);
        }
    }

    private void OpenGalleryForOrder(long orderId)
    {
        try
        {
            var services = ((App)Application.Current).Services;
            if (services == null) return;

            var galleryService = services.GetRequiredService<LalabAutoReport.Core.Interfaces.IOrderGalleryService>();
            var orderRepo = services.GetRequiredService<LalabAutoReport.Core.Interfaces.IOrderRepository>();
            var settingsRepo = services.GetRequiredService<LalabAutoReport.Core.Interfaces.ISettingsRepository>();
            var rootRepo = services.GetService<LalabAutoReport.Core.Interfaces.IRootFolderRepository>();

            var win = new OrderGalleryWindow(orderId, galleryService, orderRepo, settingsRepo, rootRepo)
            {
                Owner = Window.GetWindow(this)
            };
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không thể mở thư viện ảnh: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PreviewBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.ClosePreviewPopupCommand.Execute(null);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && ViewModel?.IsPreviewPopupOpen == true)
        {
            ViewModel.ClosePreviewPopupCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
        while (parent != null && parent is not T)
        {
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }
        return parent as T;
    }
}
