﻿using System;
using System.Collections.Generic;
using SolrNet;
using SolrNet.Impl;
using SolrNet.Impl.DocumentPropertyVisitors;
using SolrNet.Impl.FacetQuerySerializers;
using SolrNet.Impl.FieldParsers;
using SolrNet.Impl.FieldSerializers;
using SolrNet.Impl.QuerySerializers;
using SolrNet.Impl.ResponseParsers;
using SolrNet.Mapping;
using SolrNet.Mapping.Validation;
using SolrNet.Mapping.Validation.Rules;
using SolrNet.Schema;
using SolrNet.Utils;
using StructureMap.Pipeline;

namespace StructureMap.SolrNetIntegration
{
    public class SolrNetRegistry : Registry
    {
        public SolrNetRegistry(IEnumerable<ISolrServer> solrServers)
        {
            For<IReadOnlyMappingManager>().Use<MemoizingMappingManager>()
                .Ctor<IReadOnlyMappingManager>("mapper").Is(new AttributesMappingManager());
            For(typeof(ISolrDocumentActivator<>)).Use(typeof(SolrDocumentActivator<>));
            For(typeof(ISolrQueryExecuter<>)).Use(typeof(SolrQueryExecuter<>));
            For<ISolrDocumentPropertyVisitor>().Use<DefaultDocumentVisitor>();
            For<IMappingValidator>().Use<MappingValidator>();
            For<ISolrCache>().Use<NullCache>();

            RegisterParsers();
            RegisterValidationRules();
            RegisterSerializers();
            RegisterOperations();


            AddCores(solrServers);
        }

        private void RegisterValidationRules()
        {
            var validationRules = new[] {
                                            typeof(MappedPropertiesIsInSolrSchemaRule),
                                            typeof(RequiredFieldsAreMappedRule),
                                            typeof(UniqueKeyMatchesMappingRule),
                                            typeof(MultivaluedMappedToCollectionRule),
                                        };
            foreach (var validationRule in validationRules)
                For(typeof(IValidationRule)).Use(validationRule);
        }

        private void RegisterSerializers()
        {
            For(typeof(ISolrDocumentSerializer<>)).Use(typeof(SolrDocumentSerializer<>));
            For(typeof(ISolrDocumentSerializer<Dictionary<string, object>>)).Use(typeof(SolrDictionarySerializer));
            For<ISolrFieldSerializer>().Use<DefaultFieldSerializer>();
            For<ISolrQuerySerializer>().Use<DefaultQuerySerializer>();
            For<ISolrFacetQuerySerializer>().Use<DefaultFacetQuerySerializer>();
        }

        private void RegisterOperations()
        {
            For(typeof(ISolrBasicReadOnlyOperations<>)).Use(typeof(SolrBasicServer<>));
            For(typeof(ISolrBasicOperations<>)).Use(typeof(SolrBasicServer<>));
            For(typeof(ISolrReadOnlyOperations<>)).Use(typeof(SolrServer<>));
            For(typeof(ISolrOperations<>)).Use(typeof(SolrServer<>));
        }

        private void RegisterParsers()
        {
            For(typeof(ISolrDocumentResponseParser<>)).Use(typeof(SolrDocumentResponseParser<>));

            For<ISolrDocumentResponseParser<Dictionary<string, object>>>()
                .Use<SolrDictionaryDocumentResponseParser>();

            For(typeof(ISolrAbstractResponseParser<>)).Use(typeof(DefaultResponseParser<>));

            For<ISolrHeaderResponseParser>().Use<HeaderResponseParser<string>>();
            For<ISolrExtractResponseParser>().Use<ExtractResponseParser>();
            For(typeof(ISolrMoreLikeThisHandlerQueryResultsParser<>)).Use(typeof(SolrMoreLikeThisHandlerQueryResultsParser<>));
            For<ISolrFieldParser>().Use<DefaultFieldParser>();
            For<ISolrSchemaParser>().Use<SolrSchemaParser>();
            For<ISolrDIHStatusParser>().Use<SolrDIHStatusParser>();
            For<ISolrStatusResponseParser>().Use<SolrStatusResponseParser>();
            For<ISolrCoreAdmin>().Use<SolrCoreAdmin>();
        }

        /// <summary>
        /// Registers a new core in the container.
        /// This method is meant to be used after the facility initialization
        /// </summary>
        /// <param name="core"></param>
        private void RegisterCore(SolrCore core)
        {
            var coreConnectionId = core.Id + typeof(SolrConnection);

            For<ISolrConnection>().Add<SolrConnection>()
                .Named(coreConnectionId)
                .Ctor<string>("serverURL").Is(core.Url)
                .Setter(c => c.Cache).IsTheDefault();

            var ISolrQueryExecuter = typeof(ISolrQueryExecuter<>).MakeGenericType(core.DocumentType);
            var SolrQueryExecuter = typeof(SolrQueryExecuter<>).MakeGenericType(core.DocumentType);

            For(ISolrQueryExecuter).Add(SolrQueryExecuter).Named(core.Id + SolrQueryExecuter)
                .Ctor<ISolrConnection>("connection").IsNamedInstance(coreConnectionId);

            var ISolrBasicOperations = typeof(ISolrBasicOperations<>).MakeGenericType(core.DocumentType);
            var ISolrBasicReadOnlyOperations = typeof(ISolrBasicReadOnlyOperations<>).MakeGenericType(core.DocumentType);
            var SolrBasicServer = typeof(SolrBasicServer<>).MakeGenericType(core.DocumentType);

            For(ISolrBasicOperations).Add(SolrBasicServer).Named(core.Id + SolrBasicServer)
                .Ctor<ISolrConnection>("connection").IsNamedInstance(coreConnectionId)
                .Dependencies.Add("queryExecuter", new ReferencedInstance(core.Id + SolrQueryExecuter));


            For(ISolrBasicReadOnlyOperations).Add(SolrBasicServer).Named(core.Id + SolrBasicServer)
                .Ctor<ISolrConnection>("connection").IsNamedInstance(coreConnectionId)
                .Dependencies.Add("queryExecuter", new ReferencedInstance(core.Id + SolrQueryExecuter));


            var ISolrOperations = typeof(ISolrOperations<>).MakeGenericType(core.DocumentType);
            var SolrServer = typeof(SolrServer<>).MakeGenericType(core.DocumentType);
            For(ISolrOperations).Add(SolrServer).Named(core.Id)
                 .Dependencies.Add("basicServer", new ReferencedInstance(core.Id + SolrBasicServer));
        }

        private void AddCores(IEnumerable<ISolrServer> servers)
        {
            foreach (var server in servers)
            {
                var solrCore = GetCoreFrom(server);
                RegisterCore(solrCore);
            }
        }

        private static SolrCore GetCoreFrom(ISolrServer server)
        {
            var id = server.Id ?? Guid.NewGuid().ToString();
            var documentType = GetCoreDocumentType(server);
            var coreUrl = GetCoreUrl(server);
            UriValidator.ValidateHTTP(coreUrl);
            return new SolrCore(id, documentType, coreUrl);
        }

        private static string GetCoreUrl(ISolrServer server)
        {
            var url = server.Url;
            if (string.IsNullOrEmpty(url))
                throw new StructureMapConfigurationException("Core url missing in SolrNet core configuration"); // ConfigurationErrorsException("Core url missing in SolrNet core configuration");
            return url;
        }

        private static Type GetCoreDocumentType(ISolrServer server)
        {
            var documentType = server.DocumentType;

            if (string.IsNullOrEmpty(documentType))
                throw new StructureMapConfigurationException("Document type missing in SolrNet core configuration");

            Type type;

            try
            {
                type = Type.GetType(documentType);
            }
            catch (Exception e)
            {
                throw new StructureMapConfigurationException($"Error getting document type '{documentType}'", e);
            }

            if (type == null)
                throw new StructureMapConfigurationException($"Error getting document type '{documentType}'");

            return type;
        }

    }
}
