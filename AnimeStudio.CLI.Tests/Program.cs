using System.Collections.Specialized;
using AnimeStudio;
using AnimeStudio.CLI;

static class Program
{
    private static int Main()
    {
        TestEnemySettlementBattleGraphPayload();
        TestObservedEnemySettlementBattleGraphVariants();
        TestEnemySettlementBattleGraphPayloadRejectsTrailingBytes();
        TestObservedEnemyCastSkillResponsePayload();
        TestTruncatedEnemyCastSkillResponseReportsExactCursor();
        TestObservedEnemySimpleAttackPayloads();
        TestObservedEnemyCheckGameplayTagPayload();
        TestObservedUIToggleSetValuePayload();
        TestExactGuideRemainingPayloads();
        TestExactGuideCameraBlendPayloads();
        TestExactCameraControlLockEnemyPayloads();
        TestExactAbilitySystemForEnemyPartPayload();
        TestExactEnemyPartsRootPayload();
        TestManagedReferenceRegistryValidation();
        TestManagedReferenceRegistryTypeTreeGate();
        TestValidationFailureRegistryRecovery();
        TestAbilitySystemModeWeaponVisibilityProfile();
        TestAbilitySystemSkillDataBundleExactSerializedLayout();
        TestLineFollowerSerializedTypeTreeLayout();
        Console.WriteLine("Managed-reference recovery tests passed.");
        return 0;
    }

    private static void TestObservedEnemySettlementBattleGraphVariants()
    {
        var payloads = new[]
        {
            Words(
                0x3e99999a, 0x00000000, 0xb8509cb1, 0xc4707c7f,
                0x1b7ecf33, 0xf6d09833, 0x40a00000, 0x40a00000,
                0x00000000, 0x40a00000, 0x40a00000, 0x42700000,
                0x41200000, 0x00000000, 0x00000000),
            Words(
                0x3e99999a, 0x00000000, 0x00000000, 0x00000000,
                0x1b7ecf33, 0xf6d09833, 0x40a00000, 0x40a00000,
                0x00000002, 0x40a00000, 0x40a00000, 0x42700000,
                0x41200000, 0x00000000, 0x00000001, 0x00000000,
                0xdee80001, 0x4c7f6e70, 0x00000000, 0xdee80003,
                0x4c7f6e70),
            Words(
                0x3e99999a, 0x00000000, 0x0a98a2a3, 0xc0181181,
                0x1b7ecf33, 0xf6d09833, 0x41000000, 0x41000000,
                0x00000002, 0x40a00000, 0x40a00000, 0x42700000,
                0x41200000, 0x00000000, 0x00000001, 0x00000000,
                0x6fd402eb, 0x3a466e2c, 0x00000001, 0xa220020e,
                0x3a466e36, 0x6fd402ec, 0x3a466e2c),
            Words(
                0x3e99999a, 0x00000000, 0x00000000, 0x00000000,
                0x1b7ecf33, 0xf6d09833, 0x41000000, 0x41000000,
                0x00000000, 0x40a00000, 0x40a00000, 0x42700000,
                0x41200000, 0x00000000, 0x00000001, 0x00000000,
                0xec7c0018, 0x396d0d54, 0x00000003, 0x4d880a68,
                0x268b1c4b, 0x44c40003, 0x721ee060, 0xec7c0019,
                0x396d0d54, 0x4d880a69, 0x268b1c4b),
        };

        for (var i = 0; i < payloads.Length; i++)
        {
            var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
                "EnemySettlementBattleGraph/EnemySettlementBattleGraphData",
                "Beyond.Gameplay.AI",
                "Gameplay.Beyond",
                payloads[i]);
            AssertFlag(decoded, "$decoded", true, $"observed settlement payload variant {i + 1}");
        }
    }

    private static void TestEnemySettlementBattleGraphPayload()
    {
        var payload = Words(
            0x3e99999a,
            0x00000000,
            0x00000000,
            0x00000000,
            0x1b7ecf33,
            0xf6d09833,
            0x00000000,
            0x00000000,
            0x00000000,
            0x41200000,
            0x40a00000,
            0x42700000,
            0x41200000,
            0x00000000,
            0x00000000);
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "EnemySettlementBattleGraph/EnemySettlementBattleGraphData",
            "Beyond.Gameplay.AI",
            "Gameplay.Beyond",
            payload);

        AssertFlag(decoded, "$decoded", true, "settlement payload decoded");
        AssertEqual(
            "Beyond.Gameplay.AI.EnemySettlementBattleGraph/EnemySettlementBattleGraphData",
            decoded["layout"] as string,
            "settlement layout");
        AssertEqual(0.3f, Convert.ToSingle(decoded["baseInterval"]), "base interval");
        AssertEqual(10f, Convert.ToSingle(decoded["onHitTimeout"]), "on-hit timeout");
        AssertEqual(5f, Convert.ToSingle(decoded["sightRadius"]), "sight radius");
        AssertEqual(60f, Convert.ToSingle(decoded["sightAngle"]), "sight angle");
        AssertEqual(10f, Convert.ToSingle(decoded["leaveDis"]), "leave distance");
    }

    private static void TestEnemySettlementBattleGraphPayloadRejectsTrailingBytes()
    {
        var valid = Words(
            0x3e99999a,
            0, 0, 0,
            0x1b7ecf33,
            0xf6d09833,
            0, 0, 0,
            0x41200000,
            0x40a00000,
            0x42700000,
            0x41200000,
            0, 0);
        var payload = valid.Concat(new byte[4]).ToArray();
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "EnemySettlementBattleGraph/EnemySettlementBattleGraphData",
            "Beyond.Gameplay.AI",
            "Gameplay.Beyond",
            payload);

        AssertFlag(decoded, "$decoded", false, "trailing bytes must reject exact layout");
        AssertFlag(decoded, "$heuristic", true, "rejected layout remains heuristic");
    }

    private static void TestObservedEnemyCastSkillResponsePayload()
    {
        // Exact 56-byte registry payload from BB_eny_0077_agshield,
        // RefId 2669506000975823080 in the pinned export.
        var payload = Words(
            0x3dcccccd,
            0x00000021,
            0x5f796e65,
            0x37373030,
            0x7367615f,
            0x6c656968,
            0x6b735f64,
            0x306c6c69,
            0x75735f31,
            0x65656363,
            0x00000064,
            0x00000003,
            0x00000001,
            0x00000000);
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "EnemyCastSkillResponse/EnemyCastSkillResponseData",
            "Beyond.Gameplay.AI",
            "Gameplay.Beyond",
            payload);

        AssertFlag(decoded, "$decoded", true, "observed cast-skill response payload decoded");
        AssertEqual(0.1f, Convert.ToSingle(decoded["baseInterval"]), "cast-skill response base interval");
        AssertEqual(
            "eny_0077_agshield_skill01_succeed",
            decoded["skillId"] as string,
            "cast-skill response skill id");
        AssertEqual(true, Convert.ToBoolean(decoded["interruptSkill"]), "cast-skill response interrupt flag");
        AssertEqual(false, Convert.ToBoolean(decoded["waitFinish"]), "cast-skill response wait-finish flag");
    }

    private static void TestTruncatedEnemyCastSkillResponseReportsExactCursor()
    {
        var payload = Words(
            0x3dcccccd,
            0x00000021,
            0x5f796e65,
            0x37373030,
            0x7367615f,
            0x6c656968,
            0x6b735f64,
            0x306c6c69,
            0x75735f31,
            0x65656363,
            0x00000064,
            0x00000003,
            0x00000001);
        var failure = Exporter.DecodeEnemyCastSkillResponseFailureForTesting(payload);
        AssertEqual(true, Convert.ToBoolean(failure["cursorAvailable"]), "failure cursor is available");
        AssertEqual(payload.Length, Convert.ToInt32(failure["relativeCursor"]), "failure relative cursor");
        AssertEqual("waitFinish", failure["activeField"] as string, "failure active field");
        AssertEqual("interruptSkill", failure["lastCompletedField"] as string, "failure last completed field");
        AssertEqual(4, Convert.ToInt32(failure["requestedBytes"]), "failure requested bytes");
    }

    private static void TestObservedUIToggleSetValuePayload()
    {
        // Exact 120-byte registry payload from guide_group_make_battle_turret_ct,
        // RefId 7093480650259824651 in the pinned export.
        var payload = Words(
            0x0000000f,
            0x00000008,
            0x31653133,
            0x39386336,
            0x00000000,
            0x00000000,
            0x00000000,
            0x00000001,
            0x00000001,
            0x00000010,
            0x00000000,
            0x00000000,
            0x0000002f,
            0x42636146,
            0x646c6975,
            0x7473694c,
            0x656c6553,
            0x61507463,
            0x2f6c656e,
            0x6e69614d,
            0x6e6f432f,
            0x746e6574,
            0x7079542f,
            0x676f5465,
            0x00656c67,
            0xffffffff,
            0x00000000,
            0x00000000,
            0x00000001,
            0xffffffff);
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "UIToggleSetValue",
            "Beyond.Gameplay.Actions",
            "Gameplay.Beyond",
            payload);

        AssertFlag(decoded, "$decoded", true, "observed UI toggle payload decoded");
        var actionBase = decoded["actionBase"] as OrderedDictionary
            ?? throw new InvalidOperationException("UI toggle action base missing");
        AssertEqual("31e16c89", actionBase["key"] as string, "UI toggle action key");
        var togglePath = decoded["_togglePath"] as OrderedDictionary
            ?? throw new InvalidOperationException("UI toggle path parameter missing");
        AssertEqual(
            "FacBuildListSelectPanel/Main/Content/TypeToggle",
            togglePath["value"] as string,
            "UI toggle path value");
        var isOn = decoded["_isOn"] as OrderedDictionary
            ?? throw new InvalidOperationException("UI toggle bool parameter missing");
        AssertEqual(true, Convert.ToBoolean(isOn["value"]), "UI toggle enabled value");
    }

    private static void TestExactGuideRemainingPayloads()
    {
        var portableDevice = Exporter.DecodeManagedReferencePayloadForTesting(
            "CheckIsPortableDeviceActive",
            "Beyond.Gameplay",
            "Gameplay.Beyond",
            Words(
                0x00000008, 0x33336665, 0x36326263, 0x00000000,
                0x00000001, 0x00000001, 0x00000000, 0x00000000,
                0x0000001d, 0x6d657469, 0x7665645f, 0x5f656369,
                0x6c6c6162, 0x5f6e6f6f, 0x79636572, 0x5f656c63,
                0x00000031, 0xffffffff));
        AssertFlag(portableDevice, "$decoded", true, "portable-device condition decoded");
        var itemId = portableDevice["_itemId"] as OrderedDictionary
            ?? throw new InvalidOperationException("portable-device item parameter missing");
        AssertEqual("item_device_balloon_recycle_1", itemId["value"] as string, "portable-device item ID");

        var depotPanel = Exporter.DecodeManagedReferencePayloadForTesting(
            "OnDomainDepotMainPanelOpen",
            "Beyond.Gameplay.Conditions",
            "Gameplay.Beyond",
            Words(
                0x00000008, 0x37623961, 0x31623165, 0x00000000,
                0x00000001, 0x00000001, 0x00000000, 0x00000000,
                0x00000008, 0x616d6f64, 0x325f6e69, 0xffffffff,
                0x00000000, 0x00000000, 0x00000000, 0xffffffff));
        AssertFlag(depotPanel, "$decoded", true, "domain-depot panel condition decoded");
        var domainId = depotPanel["_domainId"] as OrderedDictionary
            ?? throw new InvalidOperationException("domain-depot ID parameter missing");
        AssertEqual("domain_2", domainId["value"] as string, "domain-depot ID");
        var waitAnimation = depotPanel["_waitAnimationIn"] as OrderedDictionary
            ?? throw new InvalidOperationException("domain-depot animation parameter missing");
        AssertEqual(false, Convert.ToBoolean(waitAnimation["value"]), "domain-depot wait-animation flag");

        var blueprintTab = Exporter.DecodeManagedReferencePayloadForTesting(
            "OnFacBlueprintTabOpen",
            "Beyond.Gameplay.Conditions",
            "Gameplay.Beyond",
            Words(
                0x00000008, 0x34366431, 0x32616539, 0x00000000,
                0x00000001, 0x00000001, 0x00000000, 0x00000000,
                0x00000001, 0xffffffff));
        AssertFlag(blueprintTab, "$decoded", true, "blueprint-tab condition decoded");
        var tabType = blueprintTab["_tabType"] as OrderedDictionary
            ?? throw new InvalidOperationException("blueprint-tab parameter missing");
        var tabTypeValue = tabType["value"] as OrderedDictionary
            ?? throw new InvalidOperationException("blueprint-tab value missing");
        AssertEqual(1, Convert.ToInt32(tabTypeValue["value"]), "blueprint-tab type");

        AssertGuideActionBaseOnlyDecoded(
            "ClearManualCraftFilterRecord",
            Words(
                0x0000000f, 0x00000008, 0x36336130, 0x39613238,
                0x00000000, 0x00000000, 0x00000000, 0x00000001,
                0x00000001, 0xffffffff));
        AssertGuideActionBaseOnlyDecoded(
            "FacResetBlueprintFilter",
            Words(
                0x00000011, 0x00000008, 0x39656364, 0x31396562,
                0x00000000, 0x00000000, 0x00000000, 0x00000001,
                0x00000001, 0xffffffff));

        var scrollCellArea = Exporter.DecodeManagedReferencePayloadForTesting(
            "GenUIScrollListCellArea",
            "Beyond.Gameplay.Actions",
            "Gameplay.Beyond",
            Words(
                0x00000005, 0x00000008, 0x36316635, 0x32376630,
                0x00000000, 0x00000000, 0x00000000, 0x00000001,
                0x00000001, 0xffffffff, 0x00000000, 0x00000000,
                0x00000049, 0x65766e49, 0x726f746e, 0x6e615079,
                0x432f6c65, 0x65746e6f, 0x492f746e, 0x426d6574,
                0x6f4e6761, 0x492f6564, 0x426d6574, 0x462f6761,
                0x426c6c75, 0x74492f67, 0x61426d65, 0x6e6f4367,
                0x746e6574, 0x6574492f, 0x73694c6d, 0x00000074,
                0xffffffff, 0x00000000, 0x00000000, 0x00000023,
                0xffffffff, 0x00000000, 0x00000000, 0x00000026,
                0xffffffff));
        AssertFlag(scrollCellArea, "$decoded", true, "scroll-cell action decoded");
        var listPath = scrollCellArea["_listPath"] as OrderedDictionary
            ?? throw new InvalidOperationException("scroll-cell list path missing");
        AssertEqual(
            "InventoryPanel/Content/ItemBagNode/ItemBag/FullBg/ItemBagContent/ItemList",
            listPath["value"] as string,
            "scroll-cell list path");
        AssertGuideParamIntValue(scrollCellArea, "_startIndex", 35, "scroll-cell start index");
        AssertGuideParamIntValue(scrollCellArea, "_endIndex", 38, "scroll-cell end index");
    }

    private static void AssertGuideActionBaseOnlyDecoded(string className, byte[] payload)
    {
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            className,
            "Beyond.Gameplay.Actions",
            "Gameplay.Beyond",
            payload);
        AssertFlag(decoded, "$decoded", true, $"{className} action decoded");
        _ = decoded["actionBase"] as OrderedDictionary
            ?? throw new InvalidOperationException($"{className} action base missing");
    }

    private static void AssertGuideParamIntValue(
        OrderedDictionary decoded,
        string fieldName,
        int expected,
        string label
    )
    {
        var parameter = decoded[fieldName] as OrderedDictionary
            ?? throw new InvalidOperationException($"{fieldName} parameter missing");
        var value = parameter["value"] as OrderedDictionary
            ?? throw new InvalidOperationException($"{fieldName} value missing");
        AssertEqual(expected, Convert.ToInt32(value["value"]), label);
    }

    private static void TestExactGuideCameraBlendPayloads()
    {
        var blendToPayload = Convert.FromBase64String(
            "FwAAAAgAAABmNzExZjU4NQAAAAAAAAAAAAAAAAEAAAABAAAA/////wAAAAAAAAAAWJ2YxCpZh0MlLb1C/////wAAAAAAAAAAhoNAQbYDpEMAAAAA/////wAAAAAAAAAAAAAAAP////8AAAAAAAAAAAAAAAD/////AAAAAAAAAAAAAHBC/////wAAAAAAAAAAAAAgQP////8AAAAAAAAAAAEAAAD/////AAAAAAAAAAAAAAAA/////wAAAAAAAAAAAABAQP////8AAAAAAAAAAAAAAAD/////AAAAAAAAAAABAAAA/////wAAAAAAAAAAAAAAAP////8AAAAAAAAAAAAAAAD/////AAAAAAAAAAAAAAAA/////wAAAAAAAAAAAAAAAP////8AAAAAAAAAAAAAAAD/////");
        var blendTo = Exporter.DecodeManagedReferencePayloadForTesting(
            "BlendToCameraTransformWithoutBack",
            "Beyond.Gameplay.Actions",
            "Gameplay.Beyond",
            blendToPayload);
        AssertFlag(blendTo, "$decoded", true, "camera blend-to action decoded");
        AssertFlag(blendTo, "exactTypeTreeDecoded", true, "camera blend-to exact marker");
        var alternativePoses = blendTo["_alternativeCameraPoses"] as OrderedDictionary
            ?? throw new InvalidOperationException("camera alternative-pose parameter missing");
        var poseValues = alternativePoses["value"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("camera alternative-pose list missing");
        AssertEqual(0, poseValues.Count, "camera alternative-pose count");
        var blendToCurve = blendTo["blendCurveKey"] as OrderedDictionary
            ?? throw new InvalidOperationException("camera blend-to curve parameter missing");
        AssertEqual(string.Empty, blendToCurve["value"] as string, "camera blend-to curve key");

        var blendOut = Exporter.DecodeManagedReferencePayloadForTesting(
            "BlendOutFromCamera",
            "Beyond.Gameplay.Actions",
            "Gameplay.Beyond",
            Convert.FromBase64String(
                "GAAAAAgAAAA4YzZhYWJmMgAAAAAAAAAAAAAAAAEAAAABAAAA/////wAAAAAAAAAAAAAAQP////8AAAAAAAAAAAAAAAD/////AAAAAAAAAAABAAAA/////wAAAAAAAAAAAAAAAP////8AAAAAAAAAAAAAAAD/////AAAAAAAAAAAAAAAA/////w=="));
        AssertFlag(blendOut, "$decoded", true, "camera blend-out action decoded");
        var blendOutCurve = blendOut["blendCurveKey"] as OrderedDictionary
            ?? throw new InvalidOperationException("camera blend-out curve parameter missing");
        AssertEqual(string.Empty, blendOutCurve["value"] as string, "camera blend-out curve key");
        AssertGuideParamIntValue(blendOut, "_resetType", 0, "camera blend-out reset type");

        AssertNotExactAndVisiblyIncomplete(
            Exporter.DecodeManagedReferencePayloadForTesting(
                "BlendToCameraTransformWithoutBack",
                "Beyond.Gameplay.Actions",
                "Gameplay.Beyond",
                AppendWord(blendToPayload, 0)),
            "camera blend-to trailing bytes");
    }

    private static void TestExactCameraControlLockEnemyPayloads()
    {
        var payloads = new[]
        {
            Convert.FromBase64String(
                "AQAAAAAAAAABAAAAAQAAAAAA8MEAAPBBAAAAAM3MDD8AAAA/mpkZP5qZmT4DAAAAzcxMPgIAAAAAAAAAZmbmPmILtjpiC7Y6AAAAAAAAAACrqqo+//8zQzMzMz9iC7Y6Ygu2OgAAAACrqqo+AAAAAAIAAAACAAAABAAAAAIAAAAAAAAAAAAAAKeSFkCnkhZAAAAAAAAAAAA4WhA9AACAPwAAgD9JJGQ+SSRkPgAAAADA9mY9AAAAAAIAAAACAAAABAAAAJqZmT4AAABAAACAPwcAAAACAAAAAAAAAAAAAAAAAABAAAAAQAAAAAAAAAAAAAAAAAAAgD8AAIA/AAAAAAAAAAAAAAAAAAAAAAAAAAACAAAAAgAAAAQAAAABAAAAAACAPwIAAAAAAAAAzWqqPgAAAEBOY/07AAAAAAAAAAAAAAAA0kVwQoEeTD8AAAAAAAAAAAAAAAAAAAAAAAAAAAIAAAACAAAABAAAAA=="),
            Convert.FromBase64String(
                "AQAAAAEAAAAAAAAAAAAAAAAA8MEAAPBBAAAAAM3MDD8AAAA/mpkZP5qZGT8DAAAAzcxMPgIAAAAAAAAAZmbmPmILtjpiC7Y6AAAAAAAAAACrqqo+//8zQzMzMz9iC7Y6Ygu2OgAAAACrqqo+AAAAAAIAAAACAAAABAAAAAIAAAAAAAAAAAAAAKeSFkCnkhZAAAAAAAAAAAA4WhA9AACAPwAAgD9JJGQ+SSRkPgAAAADA9mY9AAAAAAIAAAACAAAABAAAAJqZmT4AAABAAACAPwAAAAAAAAAAAgAAAAIAAAAEAAAAAAAAAM3MTD4CAAAAAAAAAM3MTD4AAAAAAAAAAAAAAAAAAAAAAAAAAAAANEMAAAA/AAAAAAAAAAAAAAAAAAAAAAAAAAACAAAAAgAAAAQAAAA="),
        };

        for (var i = 0; i < payloads.Length; i++)
        {
            var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
                "CameraControlLockEnemyConfig",
                "Beyond.Gameplay.View",
                "Gameplay.Beyond",
                payloads[i]);
            AssertFlag(decoded, "$decoded", true, $"lock-enemy camera payload {i + 1} decoded");
            AssertFlag(decoded, "exactTypeTreeDecoded", true, $"lock-enemy camera payload {i + 1} exact marker");
            AssertEqual(true, Convert.ToBoolean(decoded["limitToInputType"]), $"lock-enemy camera payload {i + 1} input limit");
            var durationCurve = decoded["changeDurationByDeltaYaw"] as OrderedDictionary
                ?? throw new InvalidOperationException("lock-enemy duration curve missing");
            var durationKeys = durationCurve["keyframes"] as List<OrderedDictionary>
                ?? throw new InvalidOperationException("lock-enemy duration keyframes missing");
            AssertEqual(2, durationKeys.Count, $"lock-enemy camera payload {i + 1} duration key count");
            var enteringCurve = decoded["enteringCurve"] as OrderedDictionary
                ?? throw new InvalidOperationException("lock-enemy entering curve missing");
            var enteringKeys = enteringCurve["keyframes"] as List<OrderedDictionary>
                ?? throw new InvalidOperationException("lock-enemy entering keyframes missing");
            AssertEqual(i == 0 ? 2 : 0, enteringKeys.Count, $"lock-enemy camera payload {i + 1} entering key count");
        }

        var invalidBool = (byte[])payloads[0].Clone();
        Buffer.BlockCopy(BitConverter.GetBytes(2), 0, invalidBool, 0, sizeof(int));
        AssertNotExactAndVisiblyIncomplete(
            Exporter.DecodeManagedReferencePayloadForTesting(
                "CameraControlLockEnemyConfig",
                "Beyond.Gameplay.View",
                "Gameplay.Beyond",
                invalidBool),
            "lock-enemy camera invalid bool");
    }

    private static void TestExactAbilitySystemForEnemyPartPayload()
    {
        // Exact 940-byte registry payload from data_eny_0081_ruanyi in the pinned export.
        var payload940 = Convert.FromBase64String(
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAEAAACgAAAAAAAAAAAAAAABAAAAAAAAAAEAAAAAAMhCAQAAAAAAoEEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAPwAAgD8AAIA/AQAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAQAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/AAAAPwAAgD8AAAAAAABoQgAAgD8AAAAAAAAAAAAAAAAA" +
                "AEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AQAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAABAAAAAAAAAAAAAAABAAAAAAAAAAAAAAABAAAAAAAAAAAA" +
                "AAAAAAAA/////wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAEAAAAAAAAAAgAAAAEAAAAAAAAAAMB5RBQAAAAAAAAAAMB5RAEAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAMhB" +
                "AAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAQAAAAAAAACXAAAAAQAAAA==");
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "AbilitySystemForEnemyPartData",
            "Beyond.Gameplay.Core",
            "Gameplay.Beyond",
            payload940);

        AssertFlag(
            decoded,
            "$decoded",
            true,
            $"enemy-part ability payload decoded ({decoded["exactTypeTreeDecodeFailure"]})");
        AssertFlag(decoded, "exactTypeTreeDecoded", true, "enemy-part ability exact marker");
        AssertEqual(
            "all inherited AbilitySystemData and derived enemy-part fields consumed",
            decoded["observedPayloadStatus"] as string,
            "enemy-part ability exact status");
        AssertEqual(true, Convert.ToBoolean(decoded["defaultEnabled"]), "enemy-part ability enabled flag");
        AssertEqual(false, Convert.ToBoolean(decoded["asIndividualInExcludeTargetProcessor"]), "enemy-part exclusion flag");
        var attributes = decoded["partAttributes"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("enemy-part attributes missing");
        AssertEqual(2, attributes.Count, "enemy-part attribute count");
        var firstType = attributes[0]["attributeType"] as OrderedDictionary
            ?? throw new InvalidOperationException("enemy-part first attribute type missing");
        var secondType = attributes[1]["attributeType"] as OrderedDictionary
            ?? throw new InvalidOperationException("enemy-part second attribute type missing");
        AssertEqual(1, Convert.ToInt32(firstType["value"]), "enemy-part first attribute type");
        AssertEqual(20, Convert.ToInt32(secondType["value"]), "enemy-part second attribute type");
        AssertEqual(999f, Convert.ToSingle(attributes[0]["value"]), "enemy-part first attribute value");
        AssertEqual(999f, Convert.ToSingle(attributes[1]["value"]), "enemy-part second attribute value");

        // Exact 928-byte registry payload from the exhaustive Persistent sweep.
        var variant928 = Exporter.DecodeManagedReferencePayloadForTesting(
            "AbilitySystemForEnemyPartData",
            "Beyond.Gameplay.Core",
            "Gameplay.Beyond",
            Convert.FromBase64String(
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIA/AAAAQAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAPwAAgD8AAIA/AQAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAQAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/AAAAPwAAgD8AAAAAAABoQgAAgD8AAAAAAAAAAAAAAAAA" +
                "AEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AQAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAABAAAAAAAAAAAAAAABAAAAAAAAAAAAAAABAAAAAAAAAAAA" +
                "AAAAAAAA/////wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAABAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAQAAAAEAAAABAAAAAABIQgAAAAAAAAAAAQAAAJgAAAAAAAAAAAAAAAAAAAAAAAdDAAAAAAAAAAABAAAA" +
                "AAAAAAAAAAABAAAAAAAAAAAAAACYAAAAAAAAAA=="));
        AssertEqual(
            "all inherited AbilitySystemData and derived enemy-part fields consumed",
            variant928["observedPayloadStatus"] as string,
            $"928-byte enemy-part exact status ({variant928["exactTypeTreeDecodeFailure"]})");
        AssertFlag(variant928, "exactTypeTreeDecoded", true, "928-byte enemy-part exact marker");
        var healthType = variant928["healthType"] as OrderedDictionary
            ?? throw new InvalidOperationException("928-byte enemy-part health type missing");
        AssertEqual(1, Convert.ToInt32(healthType["value"]), "928-byte enemy-part health type value");
        AssertEqual("unresolved", healthType["nameStatus"] as string, "928-byte enemy-part health type semantic status");

        AssertNotExactAndVisiblyIncomplete(
            Exporter.DecodeManagedReferencePayloadForTesting(
                "AbilitySystemForEnemyPartData",
                "Beyond.Gameplay.Core",
                "Gameplay.Beyond",
                AppendWord(payload940, 0)),
            "enemy-part ability trailing bytes");
    }

    private static void TestExactEnemyPartsRootPayload()
    {
        // Exact 92-byte registry payload from data_eny_0081_ruanyi in the pinned export.
        var payload = Convert.FromBase64String(
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAgAAACXAAAAmAAAAJkAAACaAAAAmwAAAJwAAAA0AAAANAAAAA4AAABCaXAwMDFf" +
                "Ul9UaGlnaAAAAQAAANiH14o=");
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "EnemyPartsRootComponentData",
            "Beyond.Gameplay.Core",
            "Gameplay.Beyond",
            payload);

        AssertFlag(decoded, "$decoded", true, "enemy-parts root payload decoded");
        AssertFlag(decoded, "exactTypeTreeDecoded", true, "enemy-parts root exact marker");
        AssertEqual(
            "all EnemyPartsRootComponentData TypeTree fields consumed",
            decoded["observedPayloadStatus"] as string,
            "enemy-parts root exact status");
        var mountPointData = decoded["mountPointData"] as OrderedDictionary
            ?? throw new InvalidOperationException("enemy-parts root mount-point dictionary missing");
        var mountPointEntries = mountPointData["entries"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("enemy-parts root mount-point entries missing");
        AssertEqual(0, mountPointEntries.Count, "enemy-parts root mount-point count");
        AssertEqual(true, Convert.ToBoolean(decoded["snapMountPointToSurface"]), "enemy-parts root snap flag");
        var snapMountPoints = decoded["needToSnapMountPoints"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("enemy-parts root snap mount-points missing");
        AssertEqual(8, snapMountPoints.Count, "enemy-parts root snap mount-point count");
        AssertEqual(151, Convert.ToInt32(snapMountPoints[0]["value"]), "enemy-parts root first snap mount-point");
        AssertEqual("Bip001_R_Thigh", decoded["partName"] as string, "enemy-parts root part name");
        var partTags = decoded["partTags"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("enemy-parts root tags missing");
        AssertEqual(1, partTags.Count, "enemy-parts root tag count");
        var tagId = partTags[0]["tagId"] as OrderedDictionary
            ?? throw new InvalidOperationException("enemy-parts root tag ID missing");
        AssertEqual("0x8ad787d8", tagId["hex"] as string, "enemy-parts root tag ID");

        var invalidBool = (byte[])payload.Clone();
        Buffer.BlockCopy(BitConverter.GetBytes(2), 0, invalidBool, 24, sizeof(int));
        AssertNotExactAndVisiblyIncomplete(
            Exporter.DecodeManagedReferencePayloadForTesting(
                "EnemyPartsRootComponentData",
                "Beyond.Gameplay.Core",
                "Gameplay.Beyond",
                invalidBool),
            "enemy-parts root invalid bool");

        AssertNotExactAndVisiblyIncomplete(
            Exporter.DecodeManagedReferencePayloadForTesting(
                "EnemyPartsRootComponentData",
                "Beyond.Gameplay.Core",
                "Gameplay.Beyond",
                payload[..^4]),
            "enemy-parts root truncated payload");
    }

    private static void TestObservedEnemySimpleAttackPayloads()
    {
        // Exact 56-byte registry payload from BB_eny_0029_lbmob_defend.
        var longPayload = Words(
            0x3dcccccd,
            0x00000024,
            0x5f796e65,
            0x39323030,
            0x6d626c5f,
            0x615f626f,
            0x63617474,
            0x726f636b,
            0x65735f65,
            0x656c7474,
            0x746e656d,
            0x40400000,
            0x00000000,
            0x00000000);
        var longDecoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "EnemySimpleAttackBehavior/EnemySimpleAttackBehaviorData",
            "Beyond.Gameplay.AI",
            "Gameplay.Beyond",
            longPayload);
        AssertFlag(longDecoded, "$decoded", true, "observed long simple-attack payload decoded");
        AssertEqual(
            "eny_0029_lbmob_attackcore_settlement",
            longDecoded["skillId"] as string,
            "long simple-attack skill id");
        AssertEqual(3f, Convert.ToSingle(longDecoded["skillRange"]), "long simple-attack range");
        AssertEqual(false, Convert.ToBoolean(longDecoded["changeCD"]), "long simple-attack change-CD flag");
        AssertEqual(0f, Convert.ToSingle(longDecoded["cd"]), "long simple-attack CD");

        // Exact 44-byte nonzero-tail payload from BB_eny_0117_klhound_cardefend.
        var nonzeroTailPayload = Words(
            0x3dcccccd,
            0x00000017,
            0x5f796e65,
            0x37313130,
            0x686c6b5f,
            0x646e756f,
            0x696b735f,
            0x00316c6c,
            0x40000000,
            0x00000001,
            0x40a00000);
        var nonzeroTailDecoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "EnemySimpleAttackBehavior/EnemySimpleAttackBehaviorData",
            "Beyond.Gameplay.AI",
            "Gameplay.Beyond",
            nonzeroTailPayload);
        AssertFlag(nonzeroTailDecoded, "$decoded", true, "observed nonzero-tail simple-attack payload decoded");
        AssertEqual(
            "eny_0117_klhound_skill1",
            nonzeroTailDecoded["skillId"] as string,
            "nonzero-tail simple-attack skill id");
        AssertEqual(2f, Convert.ToSingle(nonzeroTailDecoded["skillRange"]), "nonzero-tail simple-attack range");
        AssertEqual(true, Convert.ToBoolean(nonzeroTailDecoded["changeCD"]), "nonzero-tail simple-attack change-CD flag");
        AssertEqual(5f, Convert.ToSingle(nonzeroTailDecoded["cd"]), "nonzero-tail simple-attack CD");
    }

    private static void TestObservedEnemyCheckGameplayTagPayload()
    {
        // Exact 20-byte registry payload from BB_eny_0075_lbroshan.
        var payload = Words(
            0x00000000,
            0x00000000,
            0x00000001,
            0x00000001,
            0x9df293d9);
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "EnemyCheckGameplayTag/EnemyCheckGameplayTagData",
            "Beyond.Gameplay.AI",
            "Gameplay.Beyond",
            payload);

        AssertFlag(decoded, "$decoded", true, "observed gameplay-tag check payload decoded");
        var tagInfo = decoded["tagInfo"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("gameplay-tag check list missing");
        AssertEqual(1, tagInfo.Count, "gameplay-tag check count");
        AssertEqual(true, Convert.ToBoolean(tagInfo[0]["invert"]), "gameplay-tag check invert flag");
        var tag = tagInfo[0]["tag"] as OrderedDictionary
            ?? throw new InvalidOperationException("gameplay-tag check value missing");
        var tagId = tag["tagId"] as OrderedDictionary
            ?? throw new InvalidOperationException("gameplay-tag check ID missing");
        AssertEqual("0x9df293d9", tagId["hex"] as string, "gameplay-tag check ID");
    }

    private static void TestManagedReferenceRegistryValidation()
    {
        var valid = BuildRegistryType(BuildEntry(
            1,
            "EnemySettlementBattleGraph/EnemySettlementBattleGraphData",
            "Beyond.Gameplay.AI",
            "Gameplay.Beyond"));
        AssertEqual(
            true,
            Exporter.TryValidateManagedReferenceRegistry(valid, out var validDiagnostic),
            $"valid registry: {validDiagnostic?["reason"]}");

        var corrupt = BuildRegistryType(BuildEntry(
            9626766541,
            "",
            "\u0002",
            "eny_0046_lbshamman_attackcore_settlement"));
        AssertEqual(
            false,
            Exporter.TryValidateManagedReferenceRegistry(corrupt, out var corruptDiagnostic),
            "corrupt registry rejection");
        AssertEqual("invalidTypeHeader", corruptDiagnostic["reason"] as string, "corrupt registry diagnostic");

        var nullSentinel = BuildRegistryType(BuildEntry(-1, "", "", ""));
        AssertEqual(
            true,
            Exporter.TryValidateManagedReferenceRegistry(nullSentinel, out _),
            "null sentinel registry");
    }

    private static void TestManagedReferenceRegistryTypeTreeGate()
    {
        var ordinaryReferences = new TypeTree
        {
            m_Nodes = new List<TypeTreeNode>
            {
                new("Example", "Base", 0, false),
                new("BipedReferences", "references", 1, false),
                new("PPtr<Transform>", "root", 2, false),
                new("int", "m_FileID", 3, false),
                new("SInt64", "m_PathID", 3, false),
            },
        };
        AssertEqual(
            false,
            Exporter.IsFinalTopLevelTypeTreeField(
                ordinaryReferences,
                "references",
                "ManagedReferencesRegistry"),
            "ordinary references field is not a managed registry");

        var managedRegistry = new TypeTree
        {
            m_Nodes = new List<TypeTreeNode>
            {
                new("Example", "Base", 0, false),
                new("int", "value", 1, false),
                new("ManagedReferencesRegistry", "references", 1, false),
                new("int", "version", 2, false),
                new("vector", "RefIds", 2, false),
            },
        };
        AssertEqual(
            true,
            Exporter.IsFinalTopLevelTypeTreeField(
                managedRegistry,
                "references",
                "ManagedReferencesRegistry"),
            "final managed registry field recognized");

        managedRegistry.m_Nodes.Add(new TypeTreeNode("int", "trailing", 1, false));
        AssertEqual(
            false,
            Exporter.IsFinalTopLevelTypeTreeField(
                managedRegistry,
                "references",
                "ManagedReferencesRegistry"),
            "non-final managed registry field rejected");
    }

    private static void TestValidationFailureRegistryRecovery()
    {
        var rawData = Convert.FromBase64String(
            "AAAAAAAAAAAAAAAAAQAAAAEAAADP1NXnFRjhgB8AAABCQl9lbnlfMDA0Nl9sYnNoYW1tYW5fY2FyZGVmZW5kAAAAAAASAAAA5aGU6Ziy546p5rOV5oCq54mpAAAAAAAAXV9+cUetnYAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAAC4zx2wAQAAAAEHFGlae401AQAAAKWVSHcBAAAAAQAAAKUDIAtmORkGAgAAAAIAAAClAyALZjkZBj8AAABFbmVteVNldHRsZW1lbnRCYXR0bGVCZWhhdmlvci9FbmVteVNldHRsZW1lbnRCYXR0bGVCZWhhdmlvckRhdGEAEgAAAEJleW9uZC5HYW1lcGxheS5BSQAADwAAAEdhbWVwbGF5LkJleW9uZADNzMw9AgAAAAAAAAABAAAAAgAAACgAAABlbnlfMDA0Nl9sYnNoYW1tYW5fYXR0YWNrY29yZV9zZXR0bGVtZW50AACgQCoAAABlbnlfMDA0Nl9sYnNoYW1tYW5fYXR0YWNrcGxheWVyX3NldHRsZW1lbnQAAAAAIEEBBxRpWnuNNTkAAABFbmVteVNldHRsZW1lbnRCYXR0bGVHcmFwaC9FbmVteVNldHRsZW1lbnRCYXR0bGVHcmFwaERhdGEAAAASAAAAQmV5b25kLkdhbWVwbGF5LkFJAAAPAAAAR2FtZXBsYXkuQmV5b25kAJqZmT4AAAAAAAAAAAAAAAAzz34bM5jQ9gAAoEAAAKBAAAAAAAAAoEAAAKBAAABwQgAAIEEAAAAAAAAAAA==");
        var references = Exporter.RecoverManagedReferencesForTesting(
            rawData,
            168,
            439445549081428901,
            3858876083966576385);
        AssertFlag(references, "$decoded", true, "validation-failure registry fully decoded");
        var entries = references["RefIds"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("recovered validation-failure entries missing");
        AssertEqual(2, entries.Count, "recovered validation-failure entry count");
        AssertEqual(439445549081428901L, Convert.ToInt64(entries[0]["rid"]), "first recovered registry RID");
        AssertEqual(3858876083966576385L, Convert.ToInt64(entries[1]["rid"]), "second recovered registry RID");
        var secondType = entries[1]["type"] as OrderedDictionary
            ?? throw new InvalidOperationException("second recovered registry type missing");
        AssertEqual(
            "EnemySettlementBattleGraph/EnemySettlementBattleGraphData",
            secondType["class"] as string,
            "second recovered registry class");
    }

    private static void TestAbilitySystemModeWeaponVisibilityProfile()
    {
        var observedEmptyProfile = Exporter.DecodeAbilitySystemModeConfigForTesting(
            BuildAbilitySystemModeConfigPayload(false));
        var observedMode = GetOnlyMode(observedEmptyProfile);
        AssertEqual("Patrol", observedMode["modeId"] as string, "observed mode id");
        AssertEqual(
            false,
            Convert.ToBoolean(observedMode["overrideWeaponVisibilityProfile"]),
            "observed weapon visibility override");
        var observedProfile = observedMode["weaponVisibilityProfile"] as OrderedDictionary
            ?? throw new InvalidOperationException("observed weapon visibility profile missing");
        var observedSlots = observedProfile["slots"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("observed weapon visibility slots missing");
        AssertEqual(
            0,
            observedSlots.Count,
            "observed empty weapon visibility slots");

        var populatedProfile = Exporter.DecodeAbilitySystemModeConfigForTesting(
            BuildAbilitySystemModeConfigPayload(true, (2, true, false)));
        var populatedMode = GetOnlyMode(populatedProfile);
        var profile = populatedMode["weaponVisibilityProfile"] as OrderedDictionary
            ?? throw new InvalidOperationException("populated weapon visibility profile missing");
        var slots = profile["slots"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("populated weapon visibility slots missing");
        AssertEqual(1, slots.Count, "populated weapon visibility slot count");
        AssertEqual(2, Convert.ToInt32(slots[0]["weaponIndex"]), "weapon visibility slot index");
        AssertEqual(true, Convert.ToBoolean(slots[0]["showWhenIdle"]), "weapon visible while idle");
        AssertEqual(false, Convert.ToBoolean(slots[0]["showWhenFight"]), "weapon hidden while fighting");
    }

    private static void TestAbilitySystemSkillDataBundleExactSerializedLayout()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            for (var i = 0; i < 6; i++)
            {
                writer.Write(0);
            }
            WriteAlignedAsciiString(writer, "normal_skill");
            WriteAlignedAsciiString(writer, "ultimate_skill");
            WriteAlignedAsciiString(writer, "plunge_start");
            WriteAlignedAsciiString(writer, "plunge_end");
            WriteAlignedAsciiString(writer, "dodge_skill");
            writer.Write(2);
            writer.Write(1);
            writer.Write(9);
            writer.Write(0);
            writer.Write(1);
            writer.Write(0);
            writer.Write(1);
            writer.Write(1);
            writer.Write(1);
            WriteAlignedAsciiString(writer, "combo_power");
            writer.Write(12.5d);
            WriteAlignedAsciiString(writer, "ready");
            writer.Write(1);
            WriteAlignedAsciiString(writer, "combo_skill");
            WriteAlignedAsciiString(writer, "combo_node");
            writer.Write(1);
            writer.Write(3);
            writer.Write(1);
            WriteAlignedAsciiString(writer, "normal_skill");
            WriteAlignedAsciiString(writer, "HUD_ENEMY");
            writer.Write(1);
            WriteAlignedAsciiString(writer, "normal_skill");
            writer.Write(1);
            writer.Write(4);
        }

        var decoded = Exporter.DecodeAbilitySystemSkillDataBundleForTesting(stream.ToArray());
        AssertEqual(true, Convert.ToBoolean(decoded["enableComboSkillBlackboard"]), "combo blackboard enabled");
        AssertEqual("combo_skill", decoded["comboSkillId"] as string, "combo skill id");
        AssertEqual("HUD_ENEMY", decoded["hudPanelName"] as string, "HUD panel name");
        var blackboard = decoded["comboSkillBlackboard"] as OrderedDictionary
            ?? throw new InvalidOperationException("combo skill blackboard missing");
        AssertEqual(1, Convert.ToInt32(blackboard["count"]), "combo skill blackboard count");
        var conditions = decoded["comboSkillConditions"] as OrderedDictionary
            ?? throw new InvalidOperationException("combo skill conditions missing");
        AssertEqual(1, Convert.ToInt32(conditions["count"]), "combo skill condition count");
        var conditionEntries = conditions["entries"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("combo skill condition entries missing");
        AssertEqual(true, Convert.ToBoolean(conditionEntries[0]["comboSkillConditionImmediately"]), "combo skill condition immediate flag");
        var overrides = decoded["activeSkillTypeOverrides"] as OrderedDictionary
            ?? throw new InvalidOperationException("active skill type overrides missing");
        var entries = overrides["entries"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("active skill type override entries missing");
        AssertEqual(1, entries.Count, "active skill type override count");
        AssertEqual(4, Convert.ToInt32(entries[0]["value"]), "active skill type override value");
    }

    private static void TestLineFollowerSerializedTypeTreeLayout()
    {
        // Exact 100-byte payload from the pinned StreamingAssets corpus. The
        // TypeTree row's nominal byteSize is 26, but its two aligned UInt8
        // fields make the observed serialized stride 32 bytes.
        var payload = Convert.FromBase64String(
            "AwAAAAAAAACULMCGLCZLuwAAAACXAAAAAAAAAJgAAAACAAAAAAAAAJQsiVQIU1PKAAAAAJcAAAAAAAAAmAAAAAIAAAAAAAAAlCzs+EDebQkAAAAAlwAAAAAAAACYAAAAAgAAAA==");
        var typeTree = new TypeTree
        {
            m_Nodes = new List<TypeTreeNode>
            {
                new() { m_Level = 0, m_Type = "LineFollower", m_Name = "Base", m_ByteSize = -1, m_MetaFlag = 0x8000 },
                new() { m_Level = 1, m_Type = "LineFollowerData", m_Name = "data", m_ByteSize = -1, m_MetaFlag = 0x8000 },
                new() { m_Level = 2, m_Type = "Array", m_Name = "Array", m_ByteSize = -1, m_TypeFlags = 1, m_MetaFlag = 0x8000 },
                new() { m_Level = 3, m_Type = "int", m_Name = "size", m_ByteSize = 4 },
                new() { m_Level = 3, m_Type = "LineFollowerData", m_Name = "data", m_ByteSize = 26, m_MetaFlag = 0x8000 },
                new() { m_Level = 4, m_Type = "PPtr<$LineRenderer>", m_Name = "line", m_ByteSize = 12 },
                new() { m_Level = 5, m_Type = "int", m_Name = "m_FileID", m_ByteSize = 4, m_MetaFlag = 0x800001 },
                new() { m_Level = 5, m_Type = "SInt64", m_Name = "m_PathID", m_ByteSize = 8, m_MetaFlag = 0x800001 },
                new() { m_Level = 4, m_Type = "UInt8", m_Name = "useConfigSourceMountPoint", m_ByteSize = 1, m_MetaFlag = 0x4100 },
                new() { m_Level = 4, m_Type = "int", m_Name = "source", m_ByteSize = 4 },
                new() { m_Level = 4, m_Type = "UInt8", m_Name = "useConfigTargetMountPoint", m_ByteSize = 1, m_MetaFlag = 0x4100 },
                new() { m_Level = 4, m_Type = "int", m_Name = "target", m_ByteSize = 4 },
                new() { m_Level = 4, m_Type = "int", m_Name = "positionNum", m_ByteSize = 4 },
            },
            m_StringBuffer = Array.Empty<byte>(),
        };

        var decoded = TypeTreeHelper.ReadTypePayload(typeTree, payload, 0, payload.Length, out var bytesRead);
        AssertEqual((long)payload.Length, bytesRead, "LineFollower TypeTree bytes consumed");
        var rows = decoded["data"] as List<object>
            ?? throw new InvalidOperationException("LineFollower TypeTree rows missing");
        AssertEqual(3, rows.Count, "LineFollower TypeTree row count");
    }

    private static OrderedDictionary GetOnlyMode(OrderedDictionary modeConfig)
    {
        var modes = modeConfig["modes"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("decoded mode list missing");
        AssertEqual(1, modes.Count, "mode count");
        return modes[0];
    }

    private static byte[] BuildAbilitySystemModeConfigPayload(
        bool overrideWeaponVisibilityProfile,
        params (int WeaponIndex, bool ShowWhenIdle, bool ShowWhenFight)[] slots)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1);
        WriteAlignedAsciiString(writer, "Patrol");
        writer.Write(1);
        WriteAlignedAsciiString(writer, "default");
        WriteAlignedAsciiString(writer, "");
        writer.Write(1);
        writer.Write(1);
        WriteAlignedAsciiString(writer, "common_enemy_passive_patrol");
        writer.Write(1);
        writer.Write(1f);
        writer.Write(1);
        writer.Write(360f);
        writer.Write(0);
        writer.Write(1);
        writer.Write(0);
        writer.Write(0);
        writer.Write(1);
        WriteAlignedAsciiString(writer, "isWalk");
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        WriteAlignedAsciiString(writer, "");
        writer.Write(0);
        WriteAlignedAsciiString(writer, "");
        writer.Write(0);
        writer.Write(overrideWeaponVisibilityProfile ? 1 : 0);
        writer.Write(slots.Length);
        foreach (var slot in slots)
        {
            writer.Write(slot.WeaponIndex);
            writer.Write(slot.ShowWhenIdle ? 1 : 0);
            writer.Write(slot.ShowWhenFight ? 1 : 0);
        }
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        return stream.ToArray();
    }

    private static void WriteAlignedAsciiString(BinaryWriter writer, string value)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        while ((writer.BaseStream.Position & 3) != 0)
        {
            writer.Write((byte)0);
        }
    }

    private static OrderedDictionary BuildRegistryType(params OrderedDictionary[] entries)
    {
        return new OrderedDictionary
        {
            {
                "references",
                new OrderedDictionary
                {
                    { "version", 2 },
                    { "RefIds", entries.ToList() },
                }
            },
        };
    }

    private static OrderedDictionary BuildEntry(long rid, string className, string namespaceName, string assemblyName)
    {
        return new OrderedDictionary
        {
            { "rid", rid },
            {
                "type",
                new OrderedDictionary
                {
                    { "class", className },
                    { "ns", namespaceName },
                    { "asm", assemblyName },
                }
            },
            { "data", new OrderedDictionary() },
        };
    }

    private static byte[] Words(params uint[] words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        if (!BitConverter.IsLittleEndian)
        {
            for (var offset = 0; offset < bytes.Length; offset += sizeof(uint))
            {
                Array.Reverse(bytes, offset, sizeof(uint));
            }
        }
        return bytes;
    }

    private static byte[] AppendWord(byte[] payload, int value)
    {
        var result = new byte[payload.Length + sizeof(int)];
        Buffer.BlockCopy(payload, 0, result, 0, payload.Length);
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, result, payload.Length, sizeof(int));
        return result;
    }

    private static void AssertNotExactAndVisiblyIncomplete(OrderedDictionary dictionary, string label)
    {
        AssertFlag(dictionary, "exactTypeTreeDecoded", false, $"{label} exact marker");
        var visiblyIncomplete = new[] { "$partial", "$unparsed", "$heuristic" }
            .Any(key => dictionary.Contains(key) && dictionary[key] is bool flag && flag);
        AssertEqual(true, visiblyIncomplete, $"{label} visible incomplete marker");
    }

    private static void AssertFlag(OrderedDictionary dictionary, string key, bool expected, string label)
    {
        var actual = dictionary.Contains(key) && dictionary[key] is bool flag && flag;
        AssertEqual(expected, actual, label);
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
        }
    }
}
