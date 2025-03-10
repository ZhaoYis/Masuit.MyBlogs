﻿using Masuit.LuceneEFCore.SearchEngine.Interfaces;
using Masuit.MyBlogs.Core.Infrastructure.Repository.Interface;
using Masuit.MyBlogs.Core.Infrastructure.Services.Interface;
using Masuit.MyBlogs.Core.Models.Entity;

namespace Masuit.MyBlogs.Core.Infrastructure.Services
{
    public partial class CategoryService : BaseService<Category>, ICategoryService
    {
        public CategoryService(IBaseRepository<Category> repository, ISearchEngine<DataContext> searchEngine, ILuceneIndexSearcher searcher) : base(repository, searchEngine, searcher)
        {
        }

        /// <summary>
        /// 删除分类，并将该分类下的文章移动到新分类下
        /// </summary>
        /// <param name="id"></param>
        /// <param name="mid"></param>
        /// <returns></returns>
        public async Task<bool> Delete(int id, int mid)
        {
            var category = await GetByIdAsync(id);
            var categories = GetQuery(c => c.Path.StartsWith(category.Path)).ToList();
            var moveCat = await GetByIdAsync(mid);
            foreach (var c in categories)
            {
                for (var j = 0; j < c.Post.Count; j++)
                {
                    var p = c.Post.ElementAt(j);
                    moveCat.Post.Add(p);
                    for (var i = 0; i < p.PostHistoryVersion.Count; i++)
                    {
                        moveCat.PostHistoryVersion.Add(p.PostHistoryVersion.ElementAt(i));
                    }
                }

                for (var i = 0; i < c.PostHistoryVersion.Count; i++)
                {
                    var p = c.PostHistoryVersion.ElementAt(i);
                    p.CategoryId = moveCat.Id;
                    moveCat.PostHistoryVersion.Add(p);
                }
            }

            bool b = await DeleteByIdAsync(id) > 0;
            return b;
        }
    }
}
