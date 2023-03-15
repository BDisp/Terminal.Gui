﻿using System;

#nullable enable

namespace Terminal.Gui.Configuration {

	/// <summary>
	/// Event arguments for the <see cref="ConfigurationManager.ThemeManager"/> events.
	/// </summary>
	public class ThemeManagerEventArgs : EventArgs {
		/// <summary>
		/// The name of the new active theme..
		/// </summary>
		public string NewTheme { get; set; } = string.Empty;

		/// <summary>
		/// Initializes a new instance of <see cref="ThemeManagerEventArgs"/>
		/// </summary>
		public ThemeManagerEventArgs (string newTheme)
		{
			NewTheme = newTheme;
		}
	}
}