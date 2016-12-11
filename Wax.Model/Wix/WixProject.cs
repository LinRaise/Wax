namespace tomenglertde.Wax.Model.Wix
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Contracts;
    using System.IO;
    using System.Linq;
    using System.Text;

    using EnvDTE;

    using JetBrains.Annotations;

    using tomenglertde.Wax.Model.Mapping;
    using tomenglertde.Wax.Model.Tools;
    using tomenglertde.Wax.Model.VisualStudio;

    using Project = tomenglertde.Wax.Model.VisualStudio.Project;
    using Solution = tomenglertde.Wax.Model.VisualStudio.Solution;

    public class WixProject : Project
    {
        private static readonly string[] WixFileExtensions = { ".wxs", ".wxi" };
        private const string WaxConfigurationFileExtension = ".wax";

        [NotNull]
        private readonly EnvDTE.ProjectItem _configurationFileProjectItem;
        [NotNull]
        private readonly ProjectConfiguration _configuration;
        [NotNull]
        private readonly IList<WixSourceFile> _sourceFiles;

        public WixProject([NotNull] Solution solution, [NotNull] EnvDTE.Project project)
            : base(solution, project)
        {
            Contract.Requires(solution != null);
            Contract.Requires(project != null);

            _configurationFileProjectItem = GetConfigurationFileProjectItem(project);

            var configurationText = _configurationFileProjectItem.GetContent();

            _configuration = configurationText.Deserialize<ProjectConfiguration>();

            _sourceFiles = AllProjectItems
                .Where(item => WixFileExtensions.Contains(Path.GetExtension(item.Name) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(item => Path.GetExtension(item.Name), StringComparer.OrdinalIgnoreCase)
                .Select(item => new WixSourceFile(this, item))
                .ToList();

            Contract.Assume(_sourceFiles.Any());
            Contract.Assume(_sourceFiles.First() != null);
        }

        [NotNull]
        public IEnumerable<WixSourceFile> SourceFiles
        {
            get
            {
                Contract.Ensures(Contract.Result<IEnumerable<WixSourceFile>>() != null);

                return _sourceFiles;
            }
        }

        [NotNull]
        public IEnumerable<WixFileNode> FileNodes
        {
            get
            {
                Contract.Ensures(Contract.Result<IEnumerable<WixFileNode>>() != null);

                return _sourceFiles.SelectMany(sourceFile => sourceFile.FileNodes);
            }
        }

        [NotNull]
        public IEnumerable<WixDirectoryNode> DirectoryNodes
        {
            get
            {
                Contract.Ensures(Contract.Result<IEnumerable<WixDirectoryNode>>() != null);

                return _sourceFiles.SelectMany(sourceFile => sourceFile.DirectoryNodes);
            }
        }

        [NotNull]
        public IEnumerable<WixFeatureNode> FeatureNodes
        {
            get
            {
                Contract.Ensures(Contract.Result<IEnumerable<WixFeatureNode>>() != null);

                return _sourceFiles.SelectMany(sourceFile => sourceFile.FeatureNodes);
            }
        }

        [NotNull]
        public IEnumerable<WixComponentGroupNode> ComponentGroups
        {
            get
            {
                Contract.Ensures(Contract.Result<IEnumerable<WixComponentGroupNode>>() != null);

                return _sourceFiles.SelectMany(sourceFile => sourceFile.ComponentGroups);
            }
        }

        [NotNull]
        public IEnumerable<Project> DeployedProjects
        {
            get
            {
                Contract.Ensures(Contract.Result<IEnumerable<Project>>() != null);

                return Solution.Projects.Where(project => _configuration.DeployedProjectNames.Contains(project.UniqueName, StringComparer.OrdinalIgnoreCase));
            }
            set
            {
                Contract.Requires(value != null);

                var projects = value.ToArray();
                var removedProjects = DeployedProjects.Except(projects).ToArray();

                _configuration.DeployedProjectNames = projects.Select(project => project.UniqueName).ToArray();

                RemoveProjectReferences(removedProjects);
                AddProjectReferences(projects);

                SaveProjectConfiguration();
            }
        }

        public bool DeploySymbols
        {
            get
            {
                return _configuration.DeploySymbols;
            }
            set
            {
                _configuration.DeploySymbols = value;
            }
        }

        public bool HasChanges
        {
            get
            {
                return HasConfigurationChanges | HasSourceFileChanges;
            }
        }

        [NotNull]
        public string GetDirectoryId([NotNull] string directory)
        {
            Contract.Requires(directory != null);
            Contract.Ensures(Contract.Result<string>() != null);

            string value;

            return (_configuration.DirectoryMappings.TryGetValue(directory, out value) && (value != null)) ? value : GetDefaultId(directory);
        }

        public void UnmapDirectory([NotNull] string directory)
        {
            Contract.Requires(directory != null);

            _configuration.DirectoryMappings.Remove(directory);

            SaveProjectConfiguration();
        }

        public void MapDirectory([NotNull] string directory, [NotNull] WixDirectoryNode node)
        {
            Contract.Requires(directory != null);
            Contract.Requires(node != null);

            MapElement(directory, node, _configuration.DirectoryMappings);
        }

        public WixDirectoryNode AddDirectoryNode([NotNull] string directory)
        {
            Contract.Requires(directory != null);

            var name = Path.GetFileName(directory);
            var id = GetDirectoryId(directory);
            var parentDirectoryName = Path.GetDirectoryName(directory);
            var parentId = string.IsNullOrEmpty(directory) ? string.Empty : GetDirectoryId(parentDirectoryName);

            var parent = DirectoryNodes.FirstOrDefault(node => node.Id.Equals(parentId));

            if (parent == null)
            {
                if (!string.IsNullOrEmpty(parentId))
                {
                    parent = AddDirectoryNode(parentDirectoryName);
                }
                else
                {
                    parentId = "TODO:" + Guid.NewGuid();
                    var sourceFile = _sourceFiles.FirstOrDefault();
                    Contract.Assume(sourceFile != null);
                    return sourceFile.AddDirectory(id, name, parentId);
                }
            }

            return parent.AddDirectory(id, name);
        }

        public bool HasDefaultDirectoryId([NotNull] DirectoryMapping directoryMapping)
        {
            Contract.Requires(directoryMapping != null);
            var directory = directoryMapping.Directory;

            var id = GetDirectoryId(directory);
            var defaultId = GetDefaultId(directory);

            return id == defaultId;
        }

        [NotNull]
        public string GetFileId([NotNull] string filePath)
        {
            Contract.Requires(filePath != null);
            Contract.Ensures(Contract.Result<string>() != null);

            string value;

            return (_configuration.FileMappings.TryGetValue(filePath, out value) && value != null) ? value : GetDefaultId(filePath);
        }

        public void UnmapFile([NotNull] string filePath)
        {
            Contract.Requires(filePath != null);

            _configuration.FileMappings.Remove(filePath);

            SaveProjectConfiguration();
        }

        public void MapFile([NotNull] string filePath, [NotNull] WixFileNode node)
        {
            Contract.Requires(filePath != null);
            Contract.Requires(node != null);

            MapElement(filePath, node, _configuration.FileMappings);
        }

        [NotNull]
        public WixFileNode AddFileNode([NotNull] FileMapping fileMapping)
        {
            Contract.Requires(fileMapping != null);
            Contract.Ensures(Contract.Result<WixFileNode>() != null);

            var filePath = fileMapping.SourceName;

            var name = Path.GetFileName(filePath);
            var id = GetFileId(filePath);
            var directoryName = Path.GetDirectoryName(filePath);
            var directoryId = GetDirectoryId(directoryName);
            var directory = DirectoryNodes.FirstOrDefault(node => node.Id.Equals(directoryId, StringComparison.OrdinalIgnoreCase));
            directoryId = directory != null ? directory.Id : "TODO: unknown directory " + directoryName;

            var componentGroup = ForceComponentGroup(directoryId);

            ForceFeatureRef(componentGroup.Id);

            return componentGroup.AddFileComponent(id, name, fileMapping);
        }

        [NotNull]
        public static string GetDefaultId([NotNull] string path)
        {
            Contract.Requires(path != null);
            Contract.Ensures(Contract.Result<string>() != null);

            if (path.Length == 0)
                return "_";

            var s = new StringBuilder(path);

            for (var i = 0; i < s.Length; i++)
            {
                if (!IsValidForId(s[i]))
                {
                    s[i] = '_';
                }
            }

            if (char.IsDigit(s[0]))
            {
                s.Insert(0, '_');
            }

            return s.ToString();
        }

        [NotNull]
        private WixComponentGroupNode ForceComponentGroup([NotNull] string directoryId)
        {
            Contract.Requires(directoryId != null);
            Contract.Ensures(Contract.Result<WixComponentGroupNode>() != null);

            return ComponentGroups.FirstOrDefault(group => @group.Directory == directoryId) ?? _sourceFiles.First().AddComponentGroup(directoryId);
        }

        private void ForceFeatureRef([NotNull] string componentGroupId)
        {
            Contract.Requires(componentGroupId != null);

            if (FeatureNodes.Any(feature => feature.ComponentGroupRefs.Contains(componentGroupId)))
                return;

            var firstFeature = FeatureNodes.FirstOrDefault();
            if (firstFeature == null)
                return;

            firstFeature.AddComponentGroupRef(componentGroupId);
        }

        public bool HasDefaultFileId([NotNull] FileMapping fileMapping)
        {
            Contract.Requires(fileMapping != null);

            var filePath = fileMapping.SourceName;
            var id = GetFileId(filePath);
            var defaultId = GetDefaultId(filePath);

            return id == defaultId;
        }

        private void MapElement([NotNull] string path, [NotNull] WixNode node, [NotNull] IDictionary<string, string> mappings)
        {
            Contract.Requires(path != null);
            Contract.Requires(node != null);
            Contract.Requires(mappings != null);

            if (node.Id.Equals(GetDefaultId(path)))
                mappings.Remove(path);
            else
                mappings[path] = node.Id;

            SaveProjectConfiguration();
        }

        private static bool IsValidForId(char value)
        {
            return (value <= 'z') && (char.IsLetterOrDigit(value) || (value == '_') || (value == '.'));
        }

        private bool HasConfigurationChanges
        {
            get
            {
                return (_configuration.Serialize() != _configurationFileProjectItem.GetContent());
            }
        }

        private bool HasSourceFileChanges
        {
            get
            {
                return _sourceFiles.Any(sourceFile => sourceFile.HasChanges);
            }
        }

        private void SaveProjectConfiguration()
        {
            var configurationText = _configuration.Serialize();

            if (configurationText != _configurationFileProjectItem.GetContent())
                _configurationFileProjectItem.SetContent(configurationText);
        }

        [NotNull]
        private static ProjectItem GetConfigurationFileProjectItem([NotNull] EnvDTE.Project project)
        {
            Contract.Requires(project != null);
            Contract.Ensures(Contract.Result<ProjectItem>() != null);

            var configurationFileProjectItem = project.GetAllProjectItems().FirstOrDefault(item => WaxConfigurationFileExtension.Equals(Path.GetExtension(item.Name), StringComparison.OrdinalIgnoreCase));

            if (configurationFileProjectItem == null)
            {
                var configurationFileName = Path.ChangeExtension(project.FullName, WaxConfigurationFileExtension);

                if (!File.Exists(configurationFileName))
                    File.WriteAllText(configurationFileName, new ProjectConfiguration().Serialize());

                var projectItems = project.ProjectItems;
                Contract.Assume(projectItems != null);
                configurationFileProjectItem = projectItems.AddFromFile(configurationFileName);
                Contract.Assume(configurationFileProjectItem != null);
            }
            return configurationFileProjectItem;
        }

        [ContractInvariantMethod]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic", Justification = "Required for code contracts.")]
        private void ObjectInvariant()
        {
            Contract.Invariant(_sourceFiles != null);
            Contract.Invariant(_sourceFiles.Any());
            Contract.Invariant(_sourceFiles.First() != null);
            Contract.Invariant(_configuration != null);
            Contract.Invariant(_configurationFileProjectItem != null);
        }
    }
}