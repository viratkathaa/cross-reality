using UnityEngine;

/// <summary>
/// Placed on each pooled zone slot by TransferZone at runtime.
/// Forwards OnTriggerEnter to the parent TransferZone manager.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class ZoneTriggerReceiver : MonoBehaviour
{
    private TransferZone _manager;

    public void Initialize(TransferZone manager) => _manager = manager;

    private void OnTriggerEnter(Collider other) => _manager?.OnZoneTriggered(other);
}
