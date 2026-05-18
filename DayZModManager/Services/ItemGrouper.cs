using System;
using System.Collections.Generic;
using System.Linq;
using DayZModManager.Models;

namespace DayZModManager.Services;

/// <summary>
/// Groups items by affinity (weapons+ammo, medical, food, clothing, ...) so each AI batch
/// contains related items that can be balanced against each other.
/// </summary>
public static class ItemGrouper
{
    public enum Group
    {
        WeaponsAndAmmo,
        Medical,
        FoodDrink,
        ClothingArmor,
        ToolsEquipment,
        Building,
        Vehicles,
        Other,
    }

    private static readonly string[] MedicalKeywords =
        { "Bandage", "Saline", "Morphine", "Epinephrine", "Painkiller", "Vitamin",
          "Antibiotic", "Charcoal", "Disinfectant", "Bloodbag", "BloodBag", "IVStart", "Tetracycline", "Splint" };
    private static readonly string[] FoodKeywords =
        { "Food", "Drink", "Water", "Soda", "Cola", "Cooked", "Steak", "Can_", "Bottle",
          "Apple", "Pear", "Plum", "Banana", "Tomato", "Pepper", "Beans", "Spaghetti",
          "Sardines", "Tuna", "Peaches", "Rice", "Powder", "Cereal", "Bread" };
    private static readonly string[] ClothingKeywords =
        { "Vest", "Jacket", "Pants", "Helmet", "Boots", "Shoes", "Cap", "Hat",
          "Mask", "Gloves", "Shirt", "Coat", "Hoodie", "Sweater", "TShirt", "Skirt", "Dress",
          "Beanie", "Bandana", "Balaclava", "Armband", "Shoulder", "Plate", "PressVest" };
    private static readonly string[] ToolsKeywords =
        { "Knife", "Axe", "Hammer", "Wrench", "Screwdriver", "Pliers", "Crowbar",
          "Shovel", "Pickaxe", "Hacksaw", "Saw", "Lockpick", "Compass", "Map_",
          "GPS", "Binoculars", "Rangefinder", "Flashlight", "Lantern" };
    private static readonly string[] BuildingKeywords =
        { "BBP_", "Base", "Wood", "Stone", "Nail", "Plank", "Wire", "Fence",
          "Tent", "Sandbag", "Camo_Net", "Watchtower", "Gate", "Wall", "Roof" };
    private static readonly string[] VehicleKeywords =
        { "Wheel", "Tire", "Battery", "SparkPlug", "Glow", "RadiatorHB",
          "Engine", "Door_", "Hood_", "Trunk_", "Hatchback", "Sedan", "Truck",
          "Offroad", "Civilian", "Olga", "Ada", "Gunter", "M3S", "MH6", "Mi8" };
    private static readonly string[] AmmoKeywords =
        { "Ammo_", "Mag_", "_Mag", "Bullets", "Bullet", "Cartridge", "Pellet", "Shell",
          "_545x39", "_556x45", "_762x39", "_762x54", "_9x19", "_45ACP", "_357",
          "_9x39", "_308Win", "_300Win", "_50AE", "_22LR", "_380", "_410" };
    private static readonly string[] WeaponHints =
        { "AK", "M4", "M16", "AKS", "AKM", "SVD", "Mosin", "Winchester", "Sporter",
          "Pistol", "Rifle", "Shotgun", "Revolver", "Saiga", "Vaiga", "Magnum", "Glock",
          "MP5", "UMP", "VSS", "ASVAL", "FAL", "FN", "CZ", "Carbine", "VSD", "SVAL", "BK_" };

    public static Group Classify(string className)
    {
        if (string.IsNullOrEmpty(className)) return Group.Other;

        if (Contains(className, AmmoKeywords) || Contains(className, WeaponHints))
            return Group.WeaponsAndAmmo;
        if (Contains(className, MedicalKeywords))
            return Group.Medical;
        if (Contains(className, FoodKeywords))
            return Group.FoodDrink;
        if (Contains(className, ClothingKeywords))
            return Group.ClothingArmor;
        if (Contains(className, ToolsKeywords))
            return Group.ToolsEquipment;
        if (Contains(className, BuildingKeywords))
            return Group.Building;
        if (Contains(className, VehicleKeywords))
            return Group.Vehicles;

        return Group.Other;
    }

    private static bool Contains(string name, string[] keywords)
    {
        foreach (var k in keywords)
            if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    /// <summary>
    /// Group items by affinity then slice each group into batches of the configured size.
    /// Returns list of batches paired with their group label for logging.
    /// </summary>
    public static List<(Group Group, List<ItemEconomy> Items)> BuildBatches(
        IEnumerable<ItemEconomy> items, int batchSize)
    {
        if (batchSize < 1) batchSize = 30;

        var grouped = items
            .Where(i => i != null && !string.IsNullOrEmpty(i.ClassName))
            .GroupBy(i => Classify(i.ClassName))
            .OrderBy(g => (int)g.Key)
            .ToList();

        var batches = new List<(Group, List<ItemEconomy>)>();
        foreach (var g in grouped)
        {
            var sorted = g.OrderBy(i => i.ClassName, StringComparer.OrdinalIgnoreCase).ToList();
            for (var i = 0; i < sorted.Count; i += batchSize)
            {
                var slice = sorted.Skip(i).Take(batchSize).ToList();
                batches.Add((g.Key, slice));
            }
        }
        return batches;
    }
}
