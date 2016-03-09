﻿using Assistant;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization.Formatters.Binary;
using System.Windows.Forms;

namespace RazorEnhanced
{
	internal class Profiles
	{
		private static string m_Save = "RazorEnhanced.profiles";
		private static DataSet m_Dataset;
		internal static DataSet Dataset { get { return m_Dataset; } }

		public class ProfilesData
		{
			private string m_Name;
			public string Name { get { return m_Name; } }

			private bool m_Last;
			internal bool Last { get { return m_Last; } }

			public ProfilesData(string name, bool last)
			{
				m_Name = name;
				m_Last = last;
			}
		}

		internal static void Load()
		{
			if (m_Dataset != null)
				return;

			m_Dataset = new DataSet();
			string filename = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), m_Save);

			if (File.Exists(filename))
			{
				try
				{
					m_Dataset.RemotingFormat = SerializationFormat.Binary;
					m_Dataset.SchemaSerializationMode = SchemaSerializationMode.IncludeSchema;
					Stream stream = File.Open(filename, FileMode.Open);
					GZipStream decompress = new GZipStream(stream, CompressionMode.Decompress);
					BinaryFormatter bin = new BinaryFormatter();
					m_Dataset = bin.Deserialize(decompress) as DataSet;
					decompress.Close();
					stream.Close();
				}
				catch (Exception ex)
				{
					MessageBox.Show("Error loading " + m_Save + ": " + ex);
				}
			}
			else
			{
				// Profile
				DataTable profile = new DataTable("PROFILES");
				profile.Columns.Add("Name", typeof(string));
				profile.Columns.Add("Last", typeof(bool));
				profile.Columns.Add("PlayerName", typeof(string));
				profile.Columns.Add("PlayerSerial", typeof(int));

				DataRow profilerow = profile.NewRow();
				profilerow.ItemArray = new object[] { "default", true, "None", 0 };
				profile.Rows.Add(profilerow);

				m_Dataset.Tables.Add(profile);

				m_Dataset.AcceptChanges();
			}
		}

		// Funzioni di accesso al salvataggio
		internal static List<string> ReadAll()
		{
			List<string> profilelist = new List<string>();

			foreach (DataRow row in m_Dataset.Tables["PROFILES"].Rows)
			{
				profilelist.Add((string)row["Name"]);
			}

			return profilelist;
		}

		internal static string LastUsed()
		{
			foreach (DataRow row in m_Dataset.Tables["PROFILES"].Rows)
			{
				if ((bool)row["Last"])
					return (string)row["Name"];
			}

			return "default";
		}

		internal static void SetLast(string name)
		{
			foreach (DataRow row in m_Dataset.Tables["PROFILES"].Rows)
			{
				if ((string)row["Name"] == name)
				{
					row["Last"] = true;
				}
				else
					row["Last"] = false;
			}

			Save();
		}

		internal static void Add(string name)
		{
			DataRow row = m_Dataset.Tables["PROFILES"].NewRow();
			row["Name"] = (String)name;
			row["Last"] = (bool)true;
			row["PlayerName"] = (String)"None";
			row["PlayerSerial"] = (int)0;
			m_Dataset.Tables["PROFILES"].Rows.Add(row);

			Save();
		}

		internal static void Delete(string name)
		{
			foreach (DataRow row in m_Dataset.Tables["PROFILES"].Rows)
			{
				if ((string)row["Name"] == name)
				{
					row.Delete();
					break;
				}
			}

			Save();
		}

		internal static bool Exist(string name)
		{
			foreach (DataRow row in m_Dataset.Tables["PROFILES"].Rows)
			{
				if ((string)row["Name"] == name)
					return true;
			}

			return false;
		}

		internal static string IsLinked(int serial)
		{
			foreach (DataRow row in m_Dataset.Tables["PROFILES"].Rows)
			{
				if ((int)row["PlayerSerial"] == serial)
					return (string)row["Name"];
			}

			return null;
		}

		internal static string GetLinkName(string profilename)
		{
			foreach (DataRow row in m_Dataset.Tables["PROFILES"].Rows)
			{
				if ((string)row["Name"] == profilename)
					return (string)row["PlayerName"];
			}

			return null;
		}

		internal static void Link(int serial, string profile, string playername)
		{
			foreach (DataRow row in m_Dataset.Tables["PROFILES"].Rows)  // Slinka se gia linkato
			{
				if ((int)row["PlayerSerial"] == serial)
				{
					row["PlayerSerial"] = 0;
					row["PlayerName"] = "";
				}
			}
			foreach (DataRow row in m_Dataset.Tables["PROFILES"].Rows)  // Linko nuovo profilo
			{
				if ((string)row["Name"] == profile)
				{
					row["PlayerSerial"] = serial;
					row["PlayerName"] = playername;
				}
			}
			Save();
		}

		internal static void UnLink(string profile)
		{
			foreach (DataRow row in m_Dataset.Tables["PROFILES"].Rows)
			{
				if ((string)row["Name"] == profile)
				{
					row["PlayerSerial"] = 0;
					row["PlayerName"] = "None";
				}
			}
			Save();
		}

		internal static void Rename(string oldname, string newname)
		{
			foreach (DataRow row in m_Dataset.Tables["PROFILES"].Rows)
			{
				if ((string)row["Name"] == oldname)
				{
					row["Name"] = newname;
					break;
				}
			}
			Save();
		}

		// Funzioni richiamate dalla gui

		internal static void Refresh()
		{
			Assistant.Engine.MainWindow.ProfilesComboBox.Items.Clear();
			List<string> profilelist = ReadAll();
			foreach (string profilename in profilelist)
			{
				Assistant.Engine.MainWindow.ProfilesComboBox.Items.Add(profilename);
			}

			Assistant.Engine.MainWindow.ProfilesComboBox.SelectedIndex = Assistant.Engine.MainWindow.ProfilesComboBox.Items.IndexOf(LastUsed());
		}

		internal static void ProfileChange(string name)
		{
			// Salvo password memory
			PasswordMemory.Save();

			// Salvo parametri di uscita
			RazorEnhanced.Settings.General.SaveExitData();

			// Stop forzato di tutti gli script
			// TODO X Magneto (Funzione STOP DI SCRIPT IN ESECUZIONE)

			// Stop forzato di tutti i thread agent
			if (Assistant.Engine.MainWindow.AutolootCheckBox.Checked == true)
				Assistant.Engine.MainWindow.AutolootCheckBox.Checked = false;

			if (Assistant.Engine.MainWindow.ScavengerCheckBox.Checked == true)
				Assistant.Engine.MainWindow.ScavengerCheckBox.Checked = false;

			if (Assistant.Engine.MainWindow.OrganizerStop.Enabled == true)
				Assistant.Engine.MainWindow.OrganizerStop.PerformClick();

			if (Assistant.Engine.MainWindow.BuyCheckBox.Checked == true)
				Assistant.Engine.MainWindow.BuyCheckBox.Checked = false;

			if (Assistant.Engine.MainWindow.SellCheckBox.Checked == true)
				Assistant.Engine.MainWindow.SellCheckBox.Checked = false;

			if (Assistant.Engine.MainWindow.DressStopButton.Enabled == true)
				Assistant.Engine.MainWindow.DressStopButton.PerformClick();

			if (Assistant.Engine.MainWindow.BandageHealenableCheckBox.Checked == true)
				Assistant.Engine.MainWindow.BandageHealenableCheckBox.Checked = false;

			if (Assistant.Engine.MainWindow.DressStopButton.Enabled == true)
				Assistant.Engine.MainWindow.DressStopButton.PerformClick();

			// Stop filtri
			if (Assistant.Engine.MainWindow.AutoCarverCheckBox.Enabled == true)
				Assistant.Engine.MainWindow.AutoCarverCheckBox.Checked = false;

			if (Assistant.Engine.MainWindow.MobFilterCheckBox.Enabled == true)
				Assistant.Engine.MainWindow.MobFilterCheckBox.Checked = false;

			// Svuoto logbox e reset select index
			Assistant.Engine.MainWindow.AutoLootLogBox.Items.Clear();
			AutoLoot.AddLog("Profile Changed!");

			Assistant.Engine.MainWindow.ScavengerLogBox.Items.Clear();
			Scavenger.AddLog("Profile Changed!");

			Assistant.Engine.MainWindow.OrganizerLogBox.Items.Clear();
			Organizer.AddLog("Profile Changed!");

			Assistant.Engine.MainWindow.SellLogBox.Items.Clear();
			SellAgent.AddLog("Profile Changed!");

			Assistant.Engine.MainWindow.BuyLogBox.Items.Clear();
			BuyAgent.AddLog("Profile Changed!");

			Assistant.Engine.MainWindow.DressLogBox.Items.Clear();
			Dress.AddLog("Profile Changed!");

			Assistant.Engine.MainWindow.FriendLogBox.Items.Clear();
			Friend.AddLog("Profile Changed!");

			Assistant.Engine.MainWindow.RestockLogBox.Items.Clear();
			Restock.AddLog("Profile Changed!");

			Assistant.Engine.MainWindow.BandageHealLogBox.Items.Clear();
			BandageHeal.AddLog("Profile Changed!");

			// Cambio file
			if (name == "default")
				RazorEnhanced.Settings.ProfileFiles = "RazorEnhanced.settings";
			else
				RazorEnhanced.Settings.ProfileFiles = "RazorEnhanced." + name + ".settings";

			// Rimuovo cache password e disabilito vecchi filtri
			PasswordMemory.ClearAll();
			Assistant.Filters.Filter.DisableAll();

			// Chiuto toolbar
			if (RazorEnhanced.ToolBar.ToolBarForm != null)
				RazorEnhanced.ToolBar.ToolBarForm.Close();

			// Carico save profilo
			RazorEnhanced.Settings.Load();

			// Reinizzializzo razor
			Assistant.Engine.MainWindow.LoadSettings();

			// Riapro toollbar se le condizioni lo permettono
			RazorEnhanced.ToolBar.Open();
		}

		internal static void Save()
		{
			try
			{
				m_Dataset.AcceptChanges();

				string filename = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), m_Save);

				m_Dataset.RemotingFormat = SerializationFormat.Binary;
				m_Dataset.SchemaSerializationMode = SchemaSerializationMode.IncludeSchema;
				Stream stream = File.Create(filename);
				GZipStream compress = new GZipStream(stream, CompressionMode.Compress);
				BinaryFormatter bin = new BinaryFormatter();
				bin.Serialize(compress, m_Dataset);
				compress.Close();
				stream.Close();
			}
			catch (Exception ex)
			{
				MessageBox.Show("Error writing " + m_Save + ": " + ex);
			}
		}
	}
}