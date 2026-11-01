using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SCL.Core
{
    public class VersionResolver
    {

        private readonly string gamePath;


        public VersionResolver(string path)
        {
            gamePath = path;
        }




        // 获取完整版本继承链
        public List<string> GetVersionChain(
            string versionJson)
        {

            List<string> result =
                new();



            string? current =
                versionJson;



            while (
                !string.IsNullOrEmpty(current)
                &&
                File.Exists(current))
            {

                result.Add(current);



                using JsonDocument doc =
                    JsonDocument.Parse(
                    File.ReadAllText(current));



                if (doc.RootElement
                    .TryGetProperty(
                    "inheritsFrom",
                    out JsonElement parent))
                {

                    string id =
                        parent.GetString()
                        ??
                        "";



                    current =
                        Path.Combine(
                            gamePath,
                            "versions",
                            id,
                            id + ".json");

                }
                else
                {
                    break;
                }

            }



            return result;

        }








        // 获取游戏主类

        public string GetMainClass(
            List<string> chain)
        {

            string main =
                "net.minecraft.client.main.Main";



            foreach (string file in chain)
            {

                using JsonDocument doc =
                    JsonDocument.Parse(
                    File.ReadAllText(file));



                if (doc.RootElement
                    .TryGetProperty(
                    "mainClass",
                    out JsonElement mc))
                {

                    main =
                        mc.GetString()
                        ??
                        main;

                }

            }



            return main;

        }








        // 获取 Assets Index

        public string GetAssetIndex(
            List<string> chain)
        {

            string index =
                "legacy";



            foreach (string file in chain)
            {

                using JsonDocument doc =
                    JsonDocument.Parse(
                    File.ReadAllText(file));



                if (doc.RootElement
                    .TryGetProperty(
                    "assetIndex",
                    out JsonElement ai))
                {


                    if (ai.TryGetProperty(
                        "id",
                        out JsonElement id))
                    {

                        index =
                            id.GetString()
                            ??
                            index;

                    }

                }

            }


            return index;

        }








        // 获取所有Libraries

        public HashSet<string>
            GetLibraries(
            List<string> chain)
        {

            HashSet<string> libs =
                new();



            foreach (string file in chain)
            {

                using JsonDocument doc =
                    JsonDocument.Parse(
                    File.ReadAllText(file));



                if (!doc.RootElement
                    .TryGetProperty(
                    "libraries",
                    out JsonElement array))
                    continue;



                foreach (JsonElement lib
                    in array.EnumerateArray())
                {


                    if (!lib.TryGetProperty(
                        "downloads",
                        out JsonElement dl))
                        continue;



                    if (!dl.TryGetProperty(
                        "artifact",
                        out JsonElement artifact))
                        continue;



                    string path =
                        artifact
                        .GetProperty("path")
                        .GetString()
                        ??
                        "";



                    if (!string.IsNullOrEmpty(path))
                    {

                        string full =
                            Path.Combine(
                                gamePath,
                                "libraries",
                                path);


                        libs.Add(full);

                    }

                }

            }



            return libs;

        }








        // 判断是否Forge

        public bool IsForge(
            List<string> chain)
        {

            foreach (string file in chain)
            {

                string text =
                    File.ReadAllText(file)
                    .ToLower();



                if (text.Contains(
                    "launchwrapper")
                    ||
                    text.Contains(
                    "forge"))
                {
                    return true;
                }

            }


            return false;

        }








        // 判断是否Fabric

        public bool IsFabric(
            List<string> chain)
        {

            foreach (string file in chain)
            {

                string text =
                    File.ReadAllText(file)
                    .ToLower();



                if (text.Contains(
                    "fabric"))
                {
                    return true;
                }

            }


            return false;

        }


    }
}