﻿using Microsoft.Azure.Management.ApiManagement.ArmTemplates.Common.Extensions;
using Newtonsoft.Json;
using System.IO;
using System.Threading.Tasks;

namespace Microsoft.Azure.Management.ApiManagement.ArmTemplates.Common.FileHandlers
{
    public static class FileWriter
    {
        public static async Task SaveAsJsonAsync<T>(T item, string fileName)
        {
            var serializedItem = item.Serialize();
            await File.WriteAllTextAsync(fileName, serializedItem);
        }

        public static async Task SaveAsJsonAsync<T>(T item, string directory, string fileName)
        {
            var serializedItem = item.Serialize();
            await File.WriteAllTextAsync(Path.Combine(directory, fileName), serializedItem);
        }

        public static async Task SaveAsJsonAsync<T>(T item, params string[] pathsToFile)
        {
            var serializedItem = item.Serialize();
            await File.WriteAllTextAsync(Path.Combine(pathsToFile), serializedItem);
        }

        public static async Task SaveTextToFileAsync(string xmlContent, params string[] pathsToFile)
        {
            await File.WriteAllTextAsync(Path.Combine(pathsToFile), xmlContent);
        }

        #region Legacy writing methods

        public static void WriteJSONToFile(object template, string location)
        {
            // writes json object to provided location
            string jsonString = JsonConvert.SerializeObject(template,
                            Formatting.Indented,
                            new JsonSerializerSettings
                            {
                                NullValueHandling = NullValueHandling.Ignore
                            });
            File.WriteAllText(location, jsonString);
        }

        public static void WriteXMLToFile(string xmlContent, string location)
        {
            // writes xml content to provided location
            File.WriteAllText(location, xmlContent);
        }

        public static void CreateFolderIfNotExists(string folderLocation)
        {
            // creates directory if it does not already exist
            Directory.CreateDirectory(folderLocation);
        }

        #endregion
    }
}