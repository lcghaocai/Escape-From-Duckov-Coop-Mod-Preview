namespace EscapeFromDuckovCoopMod;

internal static class AISnapshotMetadata
{
    internal static int Signature(in AISnapshotEntry snapshot)
    {
        var hash = new HashCode();
        hash.Add(snapshot.Id);
        hash.Add(snapshot.SceneBuildIndex);
        hash.Add(snapshot.ScenePath);
        hash.Add(snapshot.SpawnPosition);
        hash.Add(snapshot.SpawnRotation);
        hash.Add(snapshot.ModelName);
        hash.Add(snapshot.CustomFaceJson);
        hash.Add(snapshot.CharacterPresetKey);
        hash.Add(snapshot.HideIfFoundEnemyName);
        hash.Add(snapshot.Team);
        hash.Add(snapshot.ShowHealthBar);
        hash.Add(snapshot.IsVehicle);
        hash.Add(snapshot.VehicleAnimationType);
        hash.Add(snapshot.VehicleWalkSpeed);
        hash.Add(snapshot.VehicleRunSpeed);

        if (snapshot.EquipmentSlots != null && snapshot.EquipmentItemTypeIds != null)
        {
            var length = Math.Min(snapshot.EquipmentSlots.Length, snapshot.EquipmentItemTypeIds.Length);
            for (var i = 0; i < length; i++)
            {
                hash.Add(snapshot.EquipmentSlots[i]);
                hash.Add(snapshot.EquipmentItemTypeIds[i]);
            }
        }

        if (snapshot.WeaponSlots != null && snapshot.WeaponItemTypeIds != null)
        {
            var length = Math.Min(snapshot.WeaponSlots.Length, snapshot.WeaponItemTypeIds.Length);
            for (var i = 0; i < length; i++)
            {
                hash.Add(snapshot.WeaponSlots[i]);
                hash.Add(snapshot.WeaponItemTypeIds[i]);
            }
        }

        if (snapshot.WeaponItemSnapshots != null && snapshot.WeaponItemSnapshots.Length > 0)
        {
            foreach (var snap in snapshot.WeaponItemSnapshots)
            {
                hash.Add(ItemTool.ComputeSnapshotHash(snap));
            }
        }

        return hash.ToHashCode();
    }

}
