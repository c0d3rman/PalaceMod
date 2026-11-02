using UnityEngine;
using System.Collections;
using System;
using System.Reflection;

namespace PalaceMod
{
    public class GoldManager : BaseManager
    {
        private KeyCode currentGoldKey = KeyCode.F5;
        private KeyCode spawnCoinsKey = KeyCode.F10;
        private GameObject coinPrefab;

        // Constants for coin spawning
        private const int COIN_COUNT = 300;
        private const float COIN_SPAWN_OFFSET = 1.5f; // Height above ground

        public override void Update()
        {
            if (Input.GetKeyDown(currentGoldKey))
            {
                AddGold();
            }

            if (Input.GetKeyDown(spawnCoinsKey))
            {
                SpawnCoins();
            }
        }

        private void AddGold()
        {
            try
            {
                var playerManager = GameObject.FindObjectOfType<PlayerManager>();
                if (playerManager?.RegisteredPlayers != null && playerManager.RegisteredPlayers.Length > 0)
                {
                    var playerInteraction = playerManager.RegisteredPlayers[0].GetComponent<PlayerInteraction>();
                    if (playerInteraction != null)
                    {
                        int goldAmount = 1000;
                        playerInteraction.AddCoin(goldAmount);
                        Plugin.ShowMessage($"Added {goldAmount} gold!");
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogError($"Error adding gold: {e}");
            }
        }

        private void SpawnCoins()
        {
            try
            {
                // Find the player
                var playerManager = GameObject.FindObjectOfType<PlayerManager>();
                if (playerManager?.RegisteredPlayers == null || playerManager.RegisteredPlayers.Length == 0)
                {
                    Plugin.ShowMessage("Could not find player!");
                    return;
                }

                var playerInteraction = playerManager.RegisteredPlayers[0].GetComponent<PlayerInteraction>();
                if (playerInteraction == null)
                {
                    Plugin.ShowMessage("Could not find player interaction component!");
                    return;
                }

                // Find a coin prefab
                GameObject coinPrefab = FindCoinPrefab();
                if (coinPrefab == null)
                {
                    Plugin.ShowMessage("Could not find coin prefab!");
                    return;
                }

                // Create a temporary CoinSpawner
                GameObject tempSpawnerObj = new GameObject("TempCoinSpawner");
                tempSpawnerObj.transform.position = playerManager.RegisteredPlayers[0].transform.position + Vector3.up * 3f;

                CoinSpawner coinSpawner = tempSpawnerObj.AddComponent<CoinSpawner>();

                // Set the coin prefab
                var coinPrefabField = typeof(CoinSpawner).GetField("coinPrefab", BindingFlags.Public | BindingFlags.Instance);
                if (coinPrefabField != null)
                {
                    coinPrefabField.SetValue(coinSpawner, coinPrefab);
                }

                // Set the interval to spawn coins faster
                var intervalField = typeof(CoinSpawner).GetField("interval", BindingFlags.Public | BindingFlags.Instance);
                if (intervalField != null)
                {
                    intervalField.SetValue(coinSpawner, 0.01f); // Very fast spawning
                }

                // Trigger the coin spawn
                var triggerMethod = typeof(CoinSpawner).GetMethod("TriggerCoinSpawn", BindingFlags.Public | BindingFlags.Instance);
                if (triggerMethod != null)
                {
                    triggerMethod.Invoke(coinSpawner, new object[] { COIN_COUNT, playerInteraction });
                    Plugin.ShowMessage($"Spawning {COIN_COUNT} coins using game's CoinSpawner!");

                    // Destroy the spawner after all coins are spawned (plus a small buffer)
                    GameObject.Destroy(tempSpawnerObj, COIN_COUNT * 0.01f + 1f);
                }
                else
                {
                    Plugin.ShowMessage("Could not find TriggerCoinSpawn method!");
                    GameObject.Destroy(tempSpawnerObj);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Error spawning coins: {e}");
            }
        }

        private GameObject FindCoinPrefab()
        {
            // Method 1: Try to find an existing CoinSpawner and get its prefab
            var existingSpawner = GameObject.FindObjectOfType<CoinSpawner>();
            if (existingSpawner != null)
            {
                var coinPrefabField = typeof(CoinSpawner).GetField("coinPrefab", BindingFlags.Public | BindingFlags.Instance);
                if (coinPrefabField != null)
                {
                    var prefab = coinPrefabField.GetValue(existingSpawner) as GameObject;
                    if (prefab != null)
                    {
                        Plugin.ShowMessage("Found coin prefab from existing CoinSpawner!");
                        return prefab;
                    }
                }
            }

            // Method 2: Try to find a component that has a 'coinPrefab' field
            var allComponents = GameObject.FindObjectsOfType<MonoBehaviour>();
            foreach (var component in allComponents)
            {
                Type type = component.GetType();
                FieldInfo coinField = type.GetField("coinPrefab", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (coinField != null)
                {
                    var value = coinField.GetValue(component);
                    if (value is GameObject)
                    {
                        Plugin.ShowMessage("Found coin prefab via reflection!");
                        return value as GameObject;
                    }
                }
            }

            // Method 3: Try to find an existing Coin and create a prefab from it
            var existingCoin = GameObject.FindObjectOfType<Coin>();
            if (existingCoin != null)
            {
                Plugin.ShowMessage("Found coin via existing Coin component!");
                return existingCoin.gameObject;
            }

            // Method 4: Try to find any object with "Coin" in its name
            var allGameObjects = GameObject.FindObjectsOfType<GameObject>();
            foreach (var obj in allGameObjects)
            {
                if (obj.name.Contains("Coin") || obj.name.Contains("coin"))
                {
                    if (obj.GetComponent<Coin>() != null)
                    {
                        Plugin.ShowMessage("Found coin by name search!");
                        return obj;
                    }
                }
            }

            return null;
        }
    }
}
