// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Files.App.Views.Settings
{
	public sealed partial class AiPage : Page
	{
		public AiPage()
		{
			InitializeComponent();
			Loaded += AiPage_Loaded;
		}

		private void AiPage_Loaded(object sender, RoutedEventArgs e)
		{
			// Reflect the persisted key/model once the controls exist (PasswordBox/combo have no bindable text).
			GatewayKeyBox.Password = ViewModel.GatewayApiKey;
			if (!string.IsNullOrEmpty(ViewModel.GatewayModel))
				GatewayModelBox.SelectedItem = ViewModel.GatewayModel;
		}

		private void GatewayKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
			=> ViewModel.SetGatewayApiKey(GatewayKeyBox.Password);

		private void GatewayModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
			=> ViewModel.SetGatewayModel(GatewayModelBox.SelectedItem as string);
	}
}
