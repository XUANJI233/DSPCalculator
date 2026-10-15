using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DSPCalculator.Logic
{
    /// <summary>
    /// 用户自定义配置项，比如：某些（可由多种配方产出的）物品需要用特定配方生产、某些物品的配方需要几级增产剂等等
    /// </summary>
    public class UserPreference
    {
        public Dictionary<int, RecipeConfig> recipeConfigs;
        public Dictionary<int, ItemConfig> itemConfigs;

        // 下列为全局属性，全局属性的优先级会被每个物品、配方的独特设定覆盖
        public int globalIncLevel; // 如果非负，则视为全局属性
        public bool globalIsInc; // 全局是否是增产模式
        public Dictionary<int, int> globalAssemblerIdByType; // 全局：每种type的强制使用工厂。参数1是type的int，参数2是工厂的itemId而非index！
        public bool globalUseIA; // 全局：所有type：默认是否使用星际组装厂
        public int globalIAType; // 全局：星际组装厂的默认特化类型，0为无特化
        public bool bluebuff;
        public bool energyBurst;
        public bool dirac;
        public bool inferior;
        public bool customizeIncMilli;
        public bool customizeAccMilli;
        public double incMilliOverride;
        public double accMilliOverride;
        public Dictionary<int, double> builtRecipes; // “本级完成”：记录每个配方单独标记的已建成量（与RecipeInfo.count同单位）。负数代表“按计划全部建成”（旧版本数据导入的已完成标记），会在求解后转换为实际数量。修改速度、增删目标时均不会重置
        public Dictionary<int, Dictionary<int, double>> builtChains; // “整条完成”：key为点击完成的物品id，value为这条产线（含上游）占用的各配方数量。按产线分别记录，取消时可以原样撤销
        public const double BUILT_FULL = -1; // builtRecipes中代表“按计划全部建成”的值
        public const int BUILT_DATA_MARKER = 0x544C4955; // 导出数据末尾的已建量数据块标记
        public const int BUILT_CHAIN_DATA_MARKER = 0x4E494843; // 导出数据末尾的整条完成数据块标记
        public bool roundUpAssemgblerNum; // 生产设施数量显示是否向上取整
        public bool solveProliferators; // 是否将增产剂作为生产线一并需要产出的物品，默认为否，即外部输入增产剂成品

        public int bpRowCount;
        public int bpResourceCoater;
        public bool bpProductCoater;
        public bool bpStationProlifSlot;
        public bool bpBeltHighest;
        public bool bpBeltTechLimit;
        public bool bpSorterHighest;
        public bool bpSorterTechLimit;
        public int bpStackSetting;
        public bool bpConnectBlackboxCoater;

        public bool showMixBeltInfo; // 是否显示混带数据


        public UserPreference()
        {
            recipeConfigs = new Dictionary<int, RecipeConfig>();
            itemConfigs = new Dictionary<int, ItemConfig>();
            builtRecipes = new Dictionary<int, double>();
            builtChains = new Dictionary<int, Dictionary<int, double>>();
            globalIncLevel = 0;
            globalIsInc = true;
            bluebuff = false;
            energyBurst = false;
            dirac = false;
            inferior = false;
            customizeIncMilli = false;
            customizeAccMilli = false;
            roundUpAssemgblerNum = DSPCalculatorPlugin.RoundUpAssemblerNum.Value;
            solveProliferators = false;
            showMixBeltInfo = false;
            incMilliOverride = 0.25;
            accMilliOverride = 1;
            globalAssemblerIdByType = new Dictionary<int, int>();
            globalUseIA = false;
            globalIAType = 0;

            bpRowCount = 2;
            bpResourceCoater = 0;
            bpProductCoater = false;
            bpStationProlifSlot = true;
            bpBeltHighest = false;
            bpBeltTechLimit = true;
            bpSorterHighest = false;
            bpSorterTechLimit = true;
            bpStackSetting = 0;
            bpConnectBlackboxCoater = true;
        }

        public void ClearWhenChangeTarget()
        {
            builtRecipes.Clear();
            builtChains.Clear();
        }

        /// <summary>
        /// 获取配方当前有效的已建量（本级完成与所有整条完成之和，不超过计划量）
        /// </summary>
        public double GetBuiltCount(RecipeInfo recipeInfo)
        {
            if (recipeInfo == null)
                return 0;
            double built = 0;
            if (builtRecipes.ContainsKey(recipeInfo.ID))
            {
                if (builtRecipes[recipeInfo.ID] < 0)
                    return recipeInfo.count;
                built += builtRecipes[recipeInfo.ID];
            }
            foreach (var chain in builtChains.Values)
            {
                if (chain.ContainsKey(recipeInfo.ID))
                    built += chain[recipeInfo.ID];
            }
            if (built > recipeInfo.count)
                return recipeInfo.count;
            return built;
        }

        /// <summary>
        /// 整条完成：将以itemId为终点的一条产线（含上游）中各配方的数量计入已建量，同一物品多次完成时累加
        /// </summary>
        public void AddBuiltChain(int itemId, Dictionary<int, double> recipeCounts)
        {
            if (!builtChains.ContainsKey(itemId))
                builtChains[itemId] = new Dictionary<int, double>();
            Dictionary<int, double> chain = builtChains[itemId];
            foreach (var kv in recipeCounts)
            {
                if (chain.ContainsKey(kv.Key))
                    chain[kv.Key] += kv.Value;
                else
                    chain[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// 取消某配方的已建量（本级完成以及所有整条完成中的这一级），不影响上游
        /// </summary>
        public void RemoveBuiltRecipe(int recipeId)
        {
            builtRecipes.Remove(recipeId);
            List<int> emptyChains = new List<int>();
            foreach (var kv in builtChains)
            {
                kv.Value.Remove(recipeId);
                if (kv.Value.Count == 0)
                    emptyChains.Add(kv.Key);
            }
            foreach (int key in emptyChains)
                builtChains.Remove(key);
        }

        /// <summary>
        /// 已建成的生产设施数量（按当前设施设置换算，未取整）
        /// </summary>
        public double GetBuiltAssemblerCount(RecipeInfo recipeInfo)
        {
            if (recipeInfo == null || recipeInfo.count <= 0)
                return 0;
            double built = GetBuiltCount(recipeInfo);
            if (built >= recipeInfo.count)
                return recipeInfo.assemblerCount;
            return recipeInfo.assemblerCount * built / recipeInfo.count;
        }

        /// <summary>
        /// 配方是否已按计划全部建成
        /// </summary>
        public bool IsRecipeFinished(RecipeInfo recipeInfo)
        {
            if (recipeInfo == null || recipeInfo.count <= 0)
                return false;
            return GetBuiltCount(recipeInfo) >= recipeInfo.count * (1 - 1e-6);
        }

        /// <summary>
        /// 将“按计划全部建成”的标记转换为当前计划的实际数量，这样之后计划量增加时，这部分只算作部分建成
        /// </summary>
        public void ResolveFullBuiltMarks(Dictionary<int, RecipeInfo> recipeInfos)
        {
            List<int> keys = builtRecipes.Where(kv => kv.Value < 0).Select(kv => kv.Key).ToList();
            foreach (int key in keys)
            {
                if (recipeInfos.ContainsKey(key) && recipeInfos[key].count > 0)
                    builtRecipes[key] = recipeInfos[key].count;
            }
        }

        public UserPreference ShallowCopy()
        {
            UserPreference copied = new UserPreference();
            copied.recipeConfigs = recipeConfigs;
            copied.itemConfigs = itemConfigs;
            copied.builtRecipes = builtRecipes;
            copied.builtChains = builtChains;
            copied.globalIncLevel = globalIncLevel;
            copied.globalIsInc = globalIsInc;
            copied.bluebuff = bluebuff;
            copied.energyBurst = energyBurst;
            copied.dirac = dirac;
            copied.inferior = inferior;
            copied.customizeIncMilli = customizeIncMilli;
            copied.customizeAccMilli = customizeAccMilli;
            copied.solveProliferators = solveProliferators;
            copied.showMixBeltInfo= showMixBeltInfo;
            copied.incMilliOverride = incMilliOverride;
            copied.accMilliOverride = accMilliOverride;
            copied.globalAssemblerIdByType = globalAssemblerIdByType;
            return copied;
        }


        public UserPreference DeepCopy()
        {
            UserPreference copied = new UserPreference();
            copied.recipeConfigs = new Dictionary<int, RecipeConfig>();
            foreach(var recipeConfig in recipeConfigs)
            {
                copied.recipeConfigs[recipeConfig.Key] = new RecipeConfig(recipeConfig.Value);
            }
            copied.itemConfigs = new Dictionary<int, ItemConfig>();
            foreach(var itemConfig in itemConfigs)
            {
                copied.itemConfigs[itemConfig.Key] = new ItemConfig(itemConfig.Value);
            }
            copied.builtRecipes = new Dictionary<int, double>();
            foreach(var builtRecipe in builtRecipes)
            {
                copied.builtRecipes[builtRecipe.Key] = builtRecipe.Value;
            }
            copied.builtChains = new Dictionary<int, Dictionary<int, double>>();
            foreach (var chain in builtChains)
            {
                copied.builtChains[chain.Key] = new Dictionary<int, double>(chain.Value);
            }
            copied.globalIncLevel = globalIncLevel;
            copied.globalIsInc = globalIsInc;
            copied.bluebuff = bluebuff;
            copied.energyBurst = energyBurst;
            copied.dirac = dirac;
            copied.inferior = inferior;
            copied.customizeIncMilli = customizeIncMilli;
            copied.customizeAccMilli = customizeAccMilli;
            copied.solveProliferators = solveProliferators;
            copied.showMixBeltInfo = showMixBeltInfo;
            copied.incMilliOverride = incMilliOverride;
            copied.accMilliOverride = accMilliOverride;
            copied.globalAssemblerIdByType = new Dictionary<int, int>();
            foreach(var globalAssemblerId in globalAssemblerIdByType)
            {
                copied.globalAssemblerIdByType[globalAssemblerId.Key] = globalAssemblerId.Value;
            }
            return copied;
        }

        public bool IsOre(int itemId)
        {
            bool isOre = CalcDB.itemDict[itemId].defaultAsOre || CalcDB.itemDict[itemId].recipes.Count == 0;
            if (itemConfigs.ContainsKey(itemId)) // 查询用户是否指定了该物品的处理规则，是否视为原矿
            {
                isOre = itemConfigs[itemId].consideredAsOre || isOre;
                if (itemConfigs[itemId].forceNotOre && CalcDB.itemDict[itemId].recipes.Count > 0)
                    isOre = false;
            }
            return isOre;
        }

        public int bpStack
        {
            get
            {
                if (bpStackSetting == 0)
                {
                    if (GameMain.history.TechUnlocked(3803))
                        return 4;
                    else if (GameMain.history.TechUnlocked(3802))
                        return 3;
                    else if (GameMain.history.TechUnlocked(3801))
                        return 2;
                    else 
                        return 1;
                }
                else
                    return bpStackSetting;
            }
        }

        public int bpSorterMk4OutputStack
        {
            get
            {
                if (bpStackSetting == 0)
                {
                    if (GameMain.history.TechUnlocked(3315))
                        return 4;
                    else if (GameMain.history.TechUnlocked(3313))
                        return 3;
                    else if (GameMain.history.TechUnlocked(3311))
                        return 2;
                    else
                        return 1;
                }
                else
                    return bpStackSetting;
            }
        }
        public int labMaxLevel
        {
            get
            {
                if (GameMain.history.TechUnlocked(3706))
                    return 15;
                else if (GameMain.history.TechUnlocked(3705))
                    return 13;
                else if (GameMain.history.TechUnlocked(3704))
                    return 11;
                else if (GameMain.history.TechUnlocked(3703))
                    return 9;
                else if (GameMain.history.TechUnlocked(3702))
                    return 7;
                else if (GameMain.history.TechUnlocked(3701))
                    return 5;
                else
                    return 3;
            }
        }
     
        /// <param name="recipeInfos">当前求解结果，用于为旧版本写出“已完成”标记，可为null</param>
        public void Export(BinaryWriter w, Dictionary<int, RecipeInfo> recipeInfos = null)
        {
            int recipeConfigCount = recipeConfigs.Count;
            w.Write(recipeConfigCount);
            foreach (var KV in recipeConfigs)
            {
                w.Write(KV.Key);
                KV.Value.Export(w);
            }
            int itemConfigCount = itemConfigs.Count;
            w.Write(itemConfigCount);
            foreach (var KV in itemConfigs)
            {
                w.Write(KV.Key);
                KV.Value.Export(w);
            }
            w.Write(globalIncLevel);
            w.Write(globalIsInc);
            w.Write(globalAssemblerIdByType.Count);
            foreach (var KV in globalAssemblerIdByType)
            {
                w.Write(KV.Key);
                w.Write(KV.Value);
            }
            w.Write(globalUseIA);
            w.Write(globalIAType);
            w.Write(bluebuff);
            w.Write(energyBurst);
            w.Write(dirac);
            w.Write(inferior);
            w.Write(customizeIncMilli);
            w.Write(customizeAccMilli);
            w.Write(incMilliOverride);
            w.Write(accMilliOverride);
            // 旧格式的已完成标记（仅写出全部建成的配方），供旧版本读取
            HashSet<int> finishedIds = new HashSet<int>();
            foreach (var KV in builtRecipes)
            {
                if (KV.Value < 0)
                    finishedIds.Add(KV.Key);
            }
            if (recipeInfos != null)
            {
                foreach (var KV in recipeInfos)
                {
                    if (IsRecipeFinished(KV.Value))
                        finishedIds.Add(KV.Key);
                }
            }
            w.Write(finishedIds.Count);
            foreach (int id in finishedIds)
            {
                w.Write(id);
                w.Write(1);
            }
            w.Write(roundUpAssemgblerNum);
            w.Write(solveProliferators);
            w.Write(bpRowCount);
            w.Write(bpResourceCoater);
            w.Write(bpProductCoater);
            w.Write(bpStationProlifSlot);
            w.Write(bpBeltHighest);
            w.Write(bpBeltTechLimit);
            w.Write(bpSorterHighest);
            w.Write(bpSorterTechLimit);
            w.Write(bpStackSetting);
            w.Write(bpConnectBlackboxCoater);
            // 已建量数据块追加在末尾，旧版本读取时会忽略
            w.Write(BUILT_DATA_MARKER);
            w.Write(builtRecipes.Count);
            foreach (var KV in builtRecipes)
            {
                w.Write(KV.Key);
                w.Write(KV.Value);
            }
            w.Write(BUILT_CHAIN_DATA_MARKER);
            w.Write(builtChains.Count);
            foreach (var chain in builtChains)
            {
                w.Write(chain.Key);
                w.Write(chain.Value.Count);
                foreach (var KV in chain.Value)
                {
                    w.Write(KV.Key);
                    w.Write(KV.Value);
                }
            }
        }
        public void Import(BinaryReader r)
        {
            recipeConfigs.Clear();
            itemConfigs.Clear();
            globalAssemblerIdByType.Clear();
            builtRecipes.Clear();
            builtChains.Clear();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int id = r.ReadInt32();
                RecipeConfig recipeConfig = new RecipeConfig(r);
                recipeConfigs[id] = recipeConfig;
            }
            count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int id = r.ReadInt32();
                ItemConfig itemConfig = new ItemConfig(id);
                itemConfig.Import(r);
                itemConfigs[id] = itemConfig;
            }
            globalIncLevel = r.ReadInt32();
            globalIsInc = r.ReadByte() > 0;
            count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int key = r.ReadInt32();
                int value = r.ReadInt32();
                globalAssemblerIdByType[key] = value;
            }
            globalUseIA = r.ReadByte() > 0;
            globalIAType = r.ReadInt32();
            bluebuff = r.ReadByte() > 0;
            energyBurst = r.ReadByte() > 0;
            dirac = r.ReadByte() > 0;
            inferior = r.ReadByte() > 0;
            customizeIncMilli = r.ReadByte() > 0;
            customizeAccMilli = r.ReadByte() > 0;
            incMilliOverride = r.ReadDouble();
            accMilliOverride = r.ReadDouble();
            count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int key = r.ReadInt32();
                r.ReadInt32();
                builtRecipes[key] = BUILT_FULL; // 旧格式只有“已完成”标记，视为按计划全部建成
            }
            roundUpAssemgblerNum = r.ReadByte() > 0;
            solveProliferators = r.ReadByte() > 0;
            bpRowCount = r.ReadInt32();
            bpResourceCoater = r.ReadInt32();
            bpProductCoater = r.ReadByte() > 0;
            bpStationProlifSlot = r.ReadByte() > 0;
            bpBeltHighest = r.ReadByte() > 0;
            bpBeltTechLimit = r.ReadByte() > 0;
            bpSorterHighest = r.ReadByte() > 0;
            bpSorterTechLimit = r.ReadByte() > 0;
            bpStackSetting = r.ReadInt32();
            bpConnectBlackboxCoater = r.ReadByte() > 0;
            // 新格式的已建量数据块（旧数据没有这部分）
            Stream stream = r.BaseStream;
            if (stream.CanSeek && stream.Length - stream.Position >= 8 && r.ReadInt32() == BUILT_DATA_MARKER)
            {
                builtRecipes.Clear();
                count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int key = r.ReadInt32();
                    double value = r.ReadDouble();
                    builtRecipes[key] = value;
                }
                if (stream.Length - stream.Position >= 8 && r.ReadInt32() == BUILT_CHAIN_DATA_MARKER)
                {
                    builtChains.Clear();
                    count = r.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        int itemId = r.ReadInt32();
                        int recipeCount = r.ReadInt32();
                        Dictionary<int, double> chain = new Dictionary<int, double>();
                        for (int j = 0; j < recipeCount; j++)
                        {
                            int key = r.ReadInt32();
                            chain[key] = r.ReadDouble();
                        }
                        builtChains[itemId] = chain;
                    }
                }
            }
        }
    }

    public class RecipeConfig
    {
        public int ID; // recipeId
        public int incLevel; // 如果非负，视为用户需要这个配方应用增产
        public int forceIncMode; // -1 为使用全局，0为强制加速，1为强制增产
        public int assemblerItemId; // 如果大于零，视为用户需要这个配方用特定的建筑生产，此处记录的是对应建筑ItemId，要去CalcDB.assemblerDict[对应建筑ItemId]这里访问。
        public bool forceUseIA; // 是否强制使用星际组装厂，为false只是代表不强制使用，还要查看assemblerItemId
        public int IAType; // 星际组装厂特化类型，0为无特化，-1代表需要读取全局

        public RecipeConfig(RecipeInfo recipeInfo)
        {
            ID = recipeInfo.ID;
            incLevel = -1; // -1为使用全局
            forceIncMode = -1;
            assemblerItemId = -1;
            forceUseIA = false;
            IAType = -1;
        }
        public RecipeConfig(RecipeConfig ori)
        {
            ID = ori.ID;
            incLevel = ori.incLevel;
            forceIncMode = ori.forceIncMode;
            assemblerItemId = ori.assemblerItemId;
            forceUseIA = ori.forceUseIA;
            IAType = ori.IAType;
        }
        public RecipeConfig(BinaryReader r)
        {
            this.Import(r);
        }

        public void Export(BinaryWriter w)
        {
            w.Write(ID);
            w.Write(incLevel);
            w.Write(forceIncMode);
            w.Write(assemblerItemId);
            w.Write(forceUseIA);
            w.Write(IAType);
        }

        public void Import(BinaryReader r)
        {
            ID = r.ReadInt32();
            incLevel = r.ReadInt32();
            forceIncMode = r.ReadInt32();
            assemblerItemId = r.ReadInt32();
            forceUseIA = r.ReadByte() > 0;
            IAType = r.ReadInt32();
        }
    }

    public class ItemConfig
    {
        public int ID; // itemId
        public int recipeID; // 如果大于零，视为用户需要这个物品（不作为副产物的部分）必须由该配方生产
        public bool consideredAsOre; // 如果为true，无论如何视为原矿，直接输入产量。如果为false，则根据itemData.defaultAsOre决定。
        public bool forceNotOre; // 如果为true，无论如何不视为原矿，如果为false，则根据itemData.defaultAsOre决定

        public ItemConfig(int itemId)
        {
            ID= itemId;
            recipeID = 0;
            consideredAsOre = false;
            forceNotOre = false;
        }

        public ItemConfig(ItemConfig ori)
        {
            ID = ori.ID;
            recipeID = ori.recipeID;
            consideredAsOre = ori.consideredAsOre;
            forceNotOre = ori.forceNotOre;
        }

        public void Export(BinaryWriter w)
        {
            w.Write(ID);
            w.Write(recipeID);
            w.Write(consideredAsOre);
            w.Write(forceNotOre);
        }
        public void Import(BinaryReader r)
        {
            ID = r.ReadInt32();
            recipeID = r.ReadInt32();
            consideredAsOre = r.ReadByte() > 0;
            forceNotOre = r.ReadByte() > 0;
        }
    }
}
