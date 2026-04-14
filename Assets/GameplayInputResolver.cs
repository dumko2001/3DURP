using StarterAssets;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public static class GameplayInputResolver
{
    public static StarterAssetsInputs FindBestInput()
    {
        var inputs = Object.FindObjectsOfType<StarterAssetsInputs>(true);
        StarterAssetsInputs bestInput = null;
        int bestScore = int.MinValue;

        foreach (var input in inputs)
        {
            if (input == null)
                continue;

            int score = 0;
            var controller = input.GetComponent<FirstPersonController>();
            var characterController = input.GetComponent<CharacterController>();

            if (controller != null)
                score += 8;
            if (characterController != null)
                score += 4;
            if (input.gameObject.activeInHierarchy)
                score += 2;
            if (input.enabled)
                score += 1;
            if (characterController != null && characterController.enabled)
                score += 2;

#if ENABLE_INPUT_SYSTEM
            var playerInput = input.GetComponent<PlayerInput>();
            if (playerInput != null)
                score += 1;
            if (playerInput != null && playerInput.enabled)
                score += 1;
#endif

            if (bestInput == null || score > bestScore)
            {
                bestInput = input;
                bestScore = score;
            }
        }

        return bestInput;
    }

    public static bool TryResolve(out StarterAssetsInputs input, out Transform playerRoot, out FirstPersonController controller)
    {
        input = FindBestInput();
        if (input == null)
        {
            playerRoot = null;
            controller = null;
            return false;
        }

        controller = input.GetComponent<FirstPersonController>();
        playerRoot = input.transform;
        return true;
    }
}