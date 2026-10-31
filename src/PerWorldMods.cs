using System;
using Terraria.ModLoader;
using PerWorldMods.UI;

namespace PerWorldMods;

public class PerWorldMods : Mod
{
	public static PerWorldMods Instance { get; private set; }

	public override void Load()
	{
		Instance = this;
		WorldPlayGate.Install();
	}

	public override void Unload()
	{
		WorldPlayGate.Uninstall();
		Instance = null;
	}
}
