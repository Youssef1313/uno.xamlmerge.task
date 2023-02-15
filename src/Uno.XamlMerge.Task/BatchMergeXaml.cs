﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// Modified for Uno support by David John Oliver, Jerome Laban
#nullable disable

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml;

namespace Uno.UI.Tasks.BatchMerge
{
    public class BatchMergeXaml_v0 : CustomTask
    {
        private List<string> _filesWritten = new();

        [Required]
        public ITaskItem[] Pages { get; set; }

        [Required]
        public ITaskItem[] MergedXamlFiles { get; set; }

        [Required]
        public ITaskItem[] XamlNamespaces { get; set; }

        [Required]
        public string ProjectFullPath { get; set; }

        [Output]
        public string[] FilesWritten
            => _filesWritten.ToArray();

        public override bool Execute()
        {
            ValidatePageMergeFileMetadata();

            if (HasLoggedErrors)
            {
                return false;
            }

            var filteredPages = Pages.ToList();
            filteredPages.RemoveAll(e => MergedXamlFiles.Any(m => FullPathComparer.Default.Equals(e, m)));
            var xamlNamespaces = XamlNamespaces.Select(item => item.ItemSpec).ToArray();

            if (MergedXamlFiles.Length > 1)
            {
                foreach (var mergedXamlFile in MergedXamlFiles)
                {
                    var mergeFileName = Path.GetFileName(mergedXamlFile.ItemSpec);

                    BatchMerger.Merge(this,
                          mergedXamlFile.ItemSpec,
                          ProjectFullPath,
                          xamlNamespaces,
                          filteredPages.Where(p => string.Equals(p.GetMetadata("MergeFile"), mergeFileName, StringComparison.OrdinalIgnoreCase)).ToArray());
                }
            }
            else if (MergedXamlFiles.Length == 1)
            {
                // Single target file, without "MergeFile" attribution

                BatchMerger.Merge(this,
                        MergedXamlFiles[0].ItemSpec,
                        ProjectFullPath,
                        xamlNamespaces,
                        filteredPages.ToArray());
            }

            return !HasLoggedErrors;
        }

        private void ValidatePageMergeFileMetadata()
        {
            if (MergedXamlFiles.Length > 1)
            {
                foreach (var page in Pages)
                {
                    if (string.IsNullOrEmpty(page.GetMetadata("MergeFile")))
                    {
                        LogError($"The page {page.ItemSpec} does not define a `MergeFile` metadata, when multiple `MergedXamlFiles` are specified.");
                    }
                }
            }
        }

        class BatchMerger
        {
            private static bool TryMergeHashUsings(string existingNamespaceString, string newNamespaceString, out string mergedNamespaceString)
            {
                if (existingNamespaceString.Equals(newNamespaceString, StringComparison.Ordinal))
                {
                    mergedNamespaceString = existingNamespaceString;
                    return true;
                }

                var (existingUri, existingUsings) = TryStripHashUsingToTheEnd(existingNamespaceString);
                var (newUri, newUsings) = TryStripHashUsingToTheEnd(newNamespaceString);
                if (!existingUri.Equals(newUri, StringComparison.Ordinal))
                {
                    mergedNamespaceString = null;
                    return false;
                }

                mergedNamespaceString = existingUri + "#using:" + string.Join(";", existingUsings.Concat(newUsings).Distinct());
                return true;

                static (string NamespaceUri, string[] Usings) TryStripHashUsingToTheEnd(string namespaceString)
                {
                    var indexOfHashUsing = namespaceString.IndexOf("#using:", StringComparison.Ordinal);
                    if (indexOfHashUsing == -1)
                    {
                        return (namespaceString, Array.Empty<string>());
                    }

                    return (namespaceString.Substring(0, indexOfHashUsing), namespaceString.Substring(indexOfHashUsing + "#using:".Length).Split(';'));
                }
            }

            internal static void Merge(
                CustomTask owner,
                string mergedXamlFile,
                string projectFullPath,
                string[] xamlNamespaces,
                ITaskItem[] pageItems)
            {
                var mergedDictionary = MergedDictionary.CreateMergedDicionary();
                List<string> pages = new();

                if (pageItems != null)
                {
                    foreach (var pageItem in pageItems)
                    {
                        var page = pageItem.ItemSpec;

                        if (File.Exists(page))
                        {
                            pages.Add(page);
                        }
                        else
                        {
                            owner.LogError($"Can't find page {page}!");
                        }
                    }
                }

                if (owner.HasLoggedErrors)
                {
                    return;
                }

                owner.LogMessage($"Merging XAML files into {mergedXamlFile}...");

                var projectBasePath = Path.GetDirectoryName(Path.GetFullPath(projectFullPath));

                var dictionary = new Dictionary<string, string>();
                var documents = new List<XmlDocument>();

                // This dictionary handles elements, e.g, <android:MyAndroid />, where the key is the XElement
                // and the value is the prefix (e.g, "android")
                var elementsToUpdate = new Dictionary<XmlElement, string>();

                // This dictionary handles attributes that are namespace declarations, e.g xmlns:android="..."
                // where the key is the XAttribute and the value is the namespace name (e.g, "android")
                var attributesToUpdate = new Dictionary<XmlAttribute, string>();

                // This dictionary handles attributes that are property prefixes, e.g, <MyElement android:MyProp="Value" />
                var propertyAttributesToUpdate = new Dictionary<XmlAttribute, string>();

                
                foreach (string page in pages)
                {
                    try
                    {
                        var document = new XmlDocument();
                        var pageContent = File.ReadAllText(page);
                        pageContent = Utils.EscapeAmpersand(pageContent);
                        document.LoadXml(pageContent);

                        foreach (XmlNode node in document.SelectNodes("descendant::node()"))
                        {
                            if (node is XmlElement element)
                            {
                                var prefix = element.GetPrefixOfNamespace(element.NamespaceURI);
                                if (xamlNamespaces.Contains(prefix))
                                {
                                    elementsToUpdate.Add(element, prefix);
                                }

                                foreach (XmlAttribute att in element.Attributes)
                                {
                                    if (att.Name.StartsWith("xmlns:"))
                                    {
                                        string name = att.LocalName;
                                        if (xamlNamespaces.Contains(name))
                                        {
                                            attributesToUpdate.Add(att, name);
                                            if (dictionary.TryGetValue(name, out var existing))
                                            {
                                                if (TryMergeHashUsings(existing, att.Value, out var merged))
                                                {
                                                    dictionary[name] = merged;
                                                }
                                                else
                                                {
                                                    throw new Exception($"Cannot merge '{existing}' with '{att.Value}' for '{name}'");
                                                }
                                            }
                                            else
                                            {
                                                dictionary.Add(name, att.Value);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        var attributePrefix = element.GetPrefixOfNamespace(att.NamespaceURI);
                                        if (xamlNamespaces.Contains(attributePrefix))
                                        {
                                            propertyAttributesToUpdate.Add(att, attributePrefix);
                                        }
                                    }
                                }
                            }
                        }

                        documents.Add(document);
                    }
                    catch (Exception)
                    {
                        owner.LogError($"Exception found when merging namespaces for page {page}!");
                        throw;
                    }
                }

                foreach (var attributeToUpdate in attributesToUpdate)
                {
                    if (dictionary.TryGetValue(attributeToUpdate.Value, out var merged))
                    {
                        attributeToUpdate.Key.Value = merged;
                    }
                }

                foreach (var propertyAttributeToUpdate in propertyAttributesToUpdate)
                {
                    if (dictionary.TryGetValue(propertyAttributeToUpdate.Value, out var merged))
                    {
                        var ownerElement = propertyAttributeToUpdate.Key.OwnerElement;
                        ownerElement.RemoveAttributeNode(propertyAttributeToUpdate.Key);
                        ownerElement.SetAttribute(propertyAttributeToUpdate.Key.LocalName, merged, propertyAttributeToUpdate.Key.Value);
                    }
                }

                foreach (var elementToUpdate in elementsToUpdate)
                {
                    if (dictionary.TryGetValue(elementToUpdate.Value, out var merged))
                    {
                        var newElement = elementToUpdate.Key.OwnerDocument.CreateElement(elementToUpdate.Key.Prefix, elementToUpdate.Key.LocalName, merged);

                        foreach (XmlNode oldNode in elementToUpdate.Key.ChildNodes.Cast<XmlNode>().ToArray())
                        {
                            newElement.AppendChild(oldNode);
                        }
                        foreach (XmlAttribute oldAttribute in elementToUpdate.Key.Attributes.Cast<XmlAttribute>().ToArray())
                        {
                            newElement.Attributes.Append(oldAttribute);
                        }

                        elementToUpdate.Key.ParentNode.ReplaceChild(newElement, elementToUpdate.Key);
                    }
                }

                for (int i = 0; i < pages.Count; i++)
                {
                    var page = pages[i];
                    var document = documents[i];
                    try
                    {
                        mergedDictionary.MergeContent(
                            content: document.OuterXml,
                            filePath: Path.GetFullPath(page)
                                .Replace(projectBasePath, "")
                                .TrimStart(Path.DirectorySeparatorChar));
                    }
                    catch (Exception)
                    {
                        owner.LogError($"Exception found when merging page {page}!");
                        throw;
                    }
                }

                mergedDictionary.FinalizeXaml();

                Directory.CreateDirectory(Path.GetDirectoryName(mergedXamlFile));
                Utils.RewriteFileIfNecessary(mergedXamlFile, mergedDictionary.ToString());
            }
        }
    }
}
