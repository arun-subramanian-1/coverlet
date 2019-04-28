using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ConsoleTables;
using Coverlet.Core;
using Coverlet.Core.Enums;
using Coverlet.Core.Reporters;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Coverlet.MSbuild.Tasks
{
    public class CoverageResultTask : Task
    {
        private string _output;
        private string _format;
        private string _threshold;
        private string _thresholdType;
        private string _thresholdStat;
        private MSBuildLogger _logger;

        [Required]
        public string Output
        {
            get { return _output; }
            set { _output = value; }
        }

        [Required]
        public string OutputFormat
        {
            get { return _format; }
            set { _format = value; }
        }

        [Required]
        public string Threshold
        {
            get { return _threshold; }
            set { _threshold = value; }
        }

        [Required]
        public string ThresholdType
        {
            get { return _thresholdType; }
            set { _thresholdType = value; }
        }

        [Required]
        public string ThresholdStat
        {
            get { return _thresholdStat; }
            set { _thresholdStat = value; }
        }

        public CoverageResultTask()
        {
            _logger = new MSBuildLogger(Log);
        }

        public override bool Execute()
        {
            try
            {
                Console.WriteLine("\nCalculating coverage result...");

                var coverage = InstrumentationTask.Coverage;
                var result = coverage.GetCoverageResult();

                var directory = Path.GetDirectoryName(_output);
                if (directory == string.Empty)
                {
                    directory = Directory.GetCurrentDirectory();
                }
                else if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var formats = _format.Split(',');
                foreach (var format in formats)
                {
                    var reporter = new ReporterFactory(format).CreateReporter();
                    if (reporter == null)
                    {
                        throw new Exception($"Specified output format '{format}' is not supported");
                    }

                    if (reporter.OutputType == ReporterOutputType.Console)
                    {
                        // Output to console
                        Console.WriteLine("  Outputting results to console");
                        Console.WriteLine(reporter.Report(result));
                    }
                    else
                    {
                        // Output to file
                        var filename = Path.GetFileName(_output);
                        filename = (filename == string.Empty) ? $"coverage.{reporter.Extension}" : filename;
                        filename = Path.HasExtension(filename) ? filename : $"{filename}.{reporter.Extension}";

                        var report = Path.Combine(directory, filename);
                        Console.WriteLine($"  Generating report '{report}'");
                        File.WriteAllText(report, reporter.Report(result));
                    }
                }

                var thresholdTypeFlags = ThresholdTypeFlags.None;
                var thresholdTypeFlagQueue = new Queue<ThresholdTypeFlags>();
                foreach (var thresholdType in _thresholdType.Split(',').Select(t => t.Trim()))
                {
                    if (thresholdType.Equals("line", StringComparison.OrdinalIgnoreCase))
                    {
                        thresholdTypeFlags |= ThresholdTypeFlags.Line;
                        thresholdTypeFlagQueue.Enqueue(ThresholdTypeFlags.Line);
                    }
                    else if (thresholdType.Equals("branch", StringComparison.OrdinalIgnoreCase))
                    {
                        thresholdTypeFlags |= ThresholdTypeFlags.Branch;
                        thresholdTypeFlagQueue.Enqueue(ThresholdTypeFlags.Branch);
                    }
                    else if (thresholdType.Equals("method", StringComparison.OrdinalIgnoreCase))
                    {
                        thresholdTypeFlags |= ThresholdTypeFlags.Method;
                        thresholdTypeFlagQueue.Enqueue(ThresholdTypeFlags.Method );
                    }
                }

                Dictionary<ThresholdTypeFlags, double> thresholdTypeFlagValues = new Dictionary<ThresholdTypeFlags, double>();
                if (_threshold.Contains(','))
                {
                    var thresholdValues = _threshold.Split(new char[] {','}, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim());
                    if(thresholdValues.Count() != thresholdTypeFlagQueue.Count())
                    {
                        throw new Exception($"Threshold type flag count ({thresholdTypeFlagQueue.Count()}) and values count ({thresholdValues.Count()}) doesnt match");
                    }

                    foreach (var threshold in thresholdValues)
                    {
                        thresholdTypeFlagValues[thresholdTypeFlagQueue.Dequeue()] = double.Parse(threshold);
                    }
                }
                else
                {
                    double thresholdValue = double.Parse(_threshold);

                    while (thresholdTypeFlagQueue.Any())
                    {
                        thresholdTypeFlagValues[thresholdTypeFlagQueue.Dequeue()] = thresholdValue;
                    }
                }

                var thresholdStat = ThresholdStatistic.Minimum;
                if (_thresholdStat.Equals("average", StringComparison.OrdinalIgnoreCase))
                {
                    thresholdStat = ThresholdStatistic.Average;
                }
                else if (_thresholdStat.Equals("total", StringComparison.OrdinalIgnoreCase))
                {
                    thresholdStat = ThresholdStatistic.Total;
                }

                var coverageTable = new ConsoleTable("Module", "Line", "Branch", "Method");
                var summary = new CoverageSummary();
                int numModules = result.Modules.Count;

                var totalLinePercent = summary.CalculateLineCoverage(result.Modules).Percent * 100;
                var totalBranchPercent = summary.CalculateBranchCoverage(result.Modules).Percent * 100;
                var totalMethodPercent = summary.CalculateMethodCoverage(result.Modules).Percent * 100;

                foreach (var module in result.Modules)
                {
                    var linePercent = summary.CalculateLineCoverage(module.Value).Percent * 100;
                    var branchPercent = summary.CalculateBranchCoverage(module.Value).Percent * 100;
                    var methodPercent = summary.CalculateMethodCoverage(module.Value).Percent * 100;

                    coverageTable.AddRow(Path.GetFileNameWithoutExtension(module.Key), $"{linePercent}%", $"{branchPercent}%", $"{methodPercent}%");
                }

                Console.WriteLine();
                Console.WriteLine(coverageTable.ToStringAlternative());

                coverageTable.Columns.Clear();
                coverageTable.Rows.Clear();

                coverageTable.AddColumn(new[] { "", "Line", "Branch", "Method" });
                coverageTable.AddRow("Total", $"{totalLinePercent}%", $"{totalBranchPercent}%", $"{totalMethodPercent}%");
                coverageTable.AddRow("Average", $"{totalLinePercent / numModules}%", $"{totalBranchPercent / numModules}%", $"{totalMethodPercent / numModules}%");

                Console.WriteLine(coverageTable.ToStringAlternative());

                thresholdTypeFlags = result.GetThresholdTypesBelowThreshold(summary, thresholdTypeFlagValues, thresholdTypeFlags, thresholdStat);
                if (thresholdTypeFlags != ThresholdTypeFlags.None)
                {
                    var exceptionMessageBuilder = new StringBuilder();
                    if ((thresholdTypeFlags & ThresholdTypeFlags.Line) != ThresholdTypeFlags.None)
                    {
                        exceptionMessageBuilder.AppendLine($"The {thresholdStat.ToString().ToLower()} line coverage is below the specified {thresholdTypeFlagValues[ThresholdTypeFlags.Line]}");
                    }

                    if ((thresholdTypeFlags & ThresholdTypeFlags.Branch) != ThresholdTypeFlags.None)
                    {
                        exceptionMessageBuilder.AppendLine($"The {thresholdStat.ToString().ToLower()} branch coverage is below the specified {thresholdTypeFlagValues[ThresholdTypeFlags.Branch]}");
                    }

                    if ((thresholdTypeFlags & ThresholdTypeFlags.Method) != ThresholdTypeFlags.None)
                    {
                        exceptionMessageBuilder.AppendLine($"The {thresholdStat.ToString().ToLower()} method coverage is below the specified {thresholdTypeFlagValues[ThresholdTypeFlags.Method]}");
                    }

                    throw new Exception(exceptionMessageBuilder.ToString());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
                return false;
            }

            return true;
        }
    }
}
